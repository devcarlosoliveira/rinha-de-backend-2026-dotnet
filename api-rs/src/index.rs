// Porta de IvfIndex: carrega index.bin (v5) e faz a busca adaptativa em int16/AVX2.
use std::fs::File;
use std::io::Read;

#[cfg(target_arch = "x86_64")]
use std::arch::x86_64::*;

const D: usize = 14;
const MAGIC: u32 = 0x484E4952; // 'R','I','N','H'
const VERSION: i32 = 5;
const MAXPROBE: usize = 512;

pub struct IvfIndex {
    pub n: usize,
    pub k: usize,
    centroids: Vec<f32>, // k*D + 16 padding
    offsets: Vec<i32>,   // k+1
    labels: Vec<u8>,     // bitset
    vectors: Vec<i16>,   // n*D + 16 padding
    avx2: bool,
}

fn read_exact_vec<T: Copy>(f: &mut File, count: usize) -> std::io::Result<Vec<T>> {
    let bytes = count * std::mem::size_of::<T>();
    let mut buf = vec![0u8; bytes];
    f.read_exact(&mut buf)?;
    let mut out: Vec<T> = Vec::with_capacity(count);
    unsafe {
        std::ptr::copy_nonoverlapping(buf.as_ptr(), out.as_mut_ptr() as *mut u8, bytes);
        out.set_len(count);
    }
    Ok(out)
}

impl IvfIndex {
    pub fn load(path: &str) -> std::io::Result<IvfIndex> {
        let mut f = File::open(path)?;
        let mut hdr = [0u8; 20];
        f.read_exact(&mut hdr)?;
        let magic = u32::from_le_bytes(hdr[0..4].try_into().unwrap());
        let version = i32::from_le_bytes(hdr[4..8].try_into().unwrap());
        let dims = i32::from_le_bytes(hdr[8..12].try_into().unwrap());
        let n = i32::from_le_bytes(hdr[12..16].try_into().unwrap()) as usize;
        let k = i32::from_le_bytes(hdr[16..20].try_into().unwrap()) as usize;
        if magic != MAGIC || version != VERSION || dims as usize != D {
            return Err(std::io::Error::new(std::io::ErrorKind::InvalidData,
                format!("index incompativel (magic={magic:x} version={version} dims={dims})")));
        }

        let mut centroids: Vec<f32> = read_exact_vec(&mut f, k * D)?;
        centroids.resize(k * D + 16, 0.0);
        let offsets: Vec<i32> = read_exact_vec(&mut f, k + 1)?;
        let labels: Vec<u8> = read_exact_vec(&mut f, (n + 7) / 8)?;
        let mut vectors: Vec<i16> = read_exact_vec(&mut f, n * D)?;
        vectors.resize(n * D + 16, 0);

        let avx2 = is_x86_feature_detected!("avx2");
        Ok(IvfIndex { n, k, centroids, offsets, labels, vectors, avx2 })
    }

    #[inline]
    fn label(&self, v: usize) -> u8 {
        (self.labels[v >> 3] >> (v & 7)) & 1
    }

    // Retorna o fraud_count (0..5). nlow buckets baratos, escala ate nhigh se nao-unanime.
    pub fn search_adaptive(&self, q: &[f32; D], nlow: usize, nhigh: usize) -> u32 {
        let nhigh = nhigh.clamp(1, MAXPROBE.min(self.k));
        let nlow = nlow.clamp(1, nhigh);

        // consulta int16 (lanes 14,15 = 0) e f32 (lanes 14,15 = 0)
        let mut qq = [0i16; 16];
        let mut qf = [0f32; 16];
        for i in 0..D {
            qq[i] = crate::vec::quantize_i16(q[i]);
            qf[i] = q[i];
        }

        // etapa 1: top nhigh centroides ascendentes
        let mut probe = [0usize; MAXPROBE];
        let mut probed = [0f32; MAXPROBE];
        let mut filled = 0usize;
        for c in 0..self.k {
            let dc = self.centroid_dist(&qf, c);
            if filled < nhigh {
                let i = filled; filled += 1;
                probed[i] = dc; probe[i] = c;
                bubble_up(&mut probed, &mut probe, i);
            } else if dc < probed[nhigh - 1] {
                let i = nhigh - 1;
                probed[i] = dc; probe[i] = c;
                bubble_up(&mut probed, &mut probe, i);
            }
        }

        // etapa 2: incremental com early-exit
        let mut bestd = [i32::MAX; 5];
        let mut bestl = [0u8; 5];
        let mut scanned = 0usize;
        for pi in 0..filled {
            let b = probe[pi];
            let start = self.offsets[b] as usize;
            let end = self.offsets[b + 1] as usize;
            for v in start..end {
                let dist = self.vec_dist(&qq, v);
                if dist < bestd[4] {
                    insert_top5(&mut bestd, &mut bestl, dist, self.label(v));
                }
            }
            scanned += 1;
            if scanned >= nlow {
                let fc: u32 = bestl.iter().map(|&x| x as u32).sum();
                if fc == 0 || fc == 5 { break; }
            }
        }
        bestl.iter().map(|&x| x as u32).sum()
    }

    #[inline]
    fn centroid_dist(&self, qf: &[f32; 16], c: usize) -> f32 {
        let cb = c * D;
        if self.avx2 {
            unsafe { centdist_avx2(qf.as_ptr(), self.centroids.as_ptr().add(cb)) }
        } else {
            let mut s = 0f32;
            for d in 0..D {
                let diff = qf[d] - self.centroids[cb + d];
                s += diff * diff;
            }
            s
        }
    }

    #[inline]
    fn vec_dist(&self, qq: &[i16; 16], v: usize) -> i32 {
        let off = v * D;
        if self.avx2 {
            unsafe { vecdist_avx2(qq.as_ptr(), self.vectors.as_ptr().add(off)) }
        } else {
            let mut s = 0i32;
            for d in 0..D {
                let diff = qq[d] as i32 - self.vectors[off + d] as i32;
                s += diff * diff;
            }
            s
        }
    }
}

#[inline]
fn bubble_up(d: &mut [f32], idx: &mut [usize], mut i: usize) {
    while i > 0 && d[i] < d[i - 1] {
        d.swap(i, i - 1);
        idx.swap(i, i - 1);
        i -= 1;
    }
}

#[inline]
fn insert_top5(d: &mut [i32; 5], l: &mut [u8; 5], nd: i32, nl: u8) {
    d[4] = nd; l[4] = nl;
    let mut i = 4;
    while i > 0 && d[i] < d[i - 1] {
        d.swap(i, i - 1);
        l.swap(i, i - 1);
        i -= 1;
    }
}

// ---- AVX2 ----

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2")]
unsafe fn vecdist_avx2(q: *const i16, v: *const i16) -> i32 {
    let mask = _mm256_setr_epi16(1,1,1,1,1,1,1,1,1,1,1,1,1,1,0,0);
    let qv = _mm256_loadu_si256(q as *const __m256i);
    let vv = _mm256_loadu_si256(v as *const __m256i);
    let d = _mm256_sub_epi16(qv, vv);
    let d = _mm256_mullo_epi16(d, mask);
    let m = _mm256_madd_epi16(d, d); // 8 x i32
    // soma horizontal de 8 i32
    let lo = _mm256_castsi256_si128(m);
    let hi = _mm256_extracti128_si256(m, 1);
    let s = _mm_add_epi32(lo, hi);
    let s = _mm_add_epi32(s, _mm_shuffle_epi32(s, 0b01_00_11_10));
    let s = _mm_add_epi32(s, _mm_shuffle_epi32(s, 0b00_00_00_01));
    _mm_cvtsi128_si32(s)
}

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2")]
unsafe fn centdist_avx2(q: *const f32, c: *const f32) -> f32 {
    let fmask = _mm256_setr_ps(1.0,1.0,1.0,1.0,1.0,1.0,0.0,0.0);
    let d0 = _mm256_sub_ps(_mm256_loadu_ps(q), _mm256_loadu_ps(c));
    let d1 = _mm256_mul_ps(
        _mm256_sub_ps(_mm256_loadu_ps(q.add(8)), _mm256_loadu_ps(c.add(8))), fmask);
    let s = _mm256_add_ps(_mm256_mul_ps(d0, d0), _mm256_mul_ps(d1, d1));
    let lo = _mm256_castps256_ps128(s);
    let hi = _mm256_extractf128_ps(s, 1);
    let s = _mm_add_ps(lo, hi);
    let s = _mm_add_ps(s, _mm_movehl_ps(s, s));
    let s = _mm_add_ss(s, _mm_shuffle_ps(s, s, 0b01));
    _mm_cvtss_f32(s)
}
