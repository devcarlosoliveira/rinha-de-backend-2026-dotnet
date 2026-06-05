// Porta de Rinha.Core: Vectorizer (14 dims), Quantizer (int16 escala 10000),
// Normalization, MccRisk, parse do payload e datas. Deve casar bit-a-bit com o .NET.

pub const DIMS: usize = 14;
pub const SCALE16: f32 = 10000.0;

// Normalization.cs
const MAX_AMOUNT: f64 = 10000.0;
const MAX_INSTALLMENTS: f32 = 12.0;
const AMOUNT_VS_AVG_RATIO: f64 = 10.0;
const MAX_MINUTES: f64 = 1440.0;
const MAX_KM: f64 = 1000.0;
const MAX_TXCOUNT24H: f32 = 20.0;
const MAX_MERCHANT_AVG: f64 = 10000.0;

#[inline]
fn clamp01(x: f32) -> f32 {
    if x < 0.0 { 0.0 } else if x > 1.0 { 1.0 } else { x }
}

// MccRisk.cs — fora da tabela = 0.5
fn mcc_risk(mcc: Option<&str>) -> f32 {
    match mcc {
        Some("5411") => 0.15,
        Some("5812") => 0.30,
        Some("5912") => 0.20,
        Some("5944") => 0.45,
        Some("7801") => 0.80,
        Some("7802") => 0.75,
        Some("7995") => 0.85,
        Some("4511") => 0.35,
        Some("5311") => 0.25,
        Some("5999") => 0.50,
        _ => 0.50,
    }
}

// ---- datas ISO8601 -> (epoch_secs f64 UTC, hour UTC, day_of_week Mon=0..Sun=6) ----

// days desde 1970-01-01 (algoritmo de Howard Hinnant)
fn days_from_civil(y: i64, m: i64, d: i64) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

#[derive(Clone, Copy)]
pub struct Instant {
    pub epoch_secs: f64, // UTC, com fracao
    pub hour: i32,       // UTC 0..23
    pub dow_mon0: i32,   // Mon=0..Sun=6
}

// Parse "YYYY-MM-DDTHH:MM:SS[.fff][Z|±HH:MM]" -> Instant (UTC). None se invalido.
fn parse_instant(s: &str) -> Option<Instant> {
    let b = s.as_bytes();
    if b.len() < 19 { return None; }
    let num = |a: usize, n: usize| -> Option<i64> {
        let mut v: i64 = 0;
        for i in a..a + n {
            let c = b[i];
            if !c.is_ascii_digit() { return None; }
            v = v * 10 + (c - b'0') as i64;
        }
        Some(v)
    };
    let year = num(0, 4)?;
    if b[4] != b'-' || b[7] != b'-' || b[10] != b'T' || b[13] != b':' || b[16] != b':' { return None; }
    let month = num(5, 2)?;
    let day = num(8, 2)?;
    let hh = num(11, 2)?;
    let mm = num(14, 2)?;
    let ss = num(17, 2)?;

    // fracao opcional e offset
    let mut i = 19usize;
    let mut frac = 0.0f64;
    if i < b.len() && b[i] == b'.' {
        i += 1;
        let start = i;
        let mut f = 0.0f64;
        let mut scale = 1.0f64;
        while i < b.len() && b[i].is_ascii_digit() {
            scale *= 10.0;
            f = f * 10.0 + (b[i] - b'0') as f64;
            i += 1;
        }
        if i > start { frac = f / scale; }
    }
    // offset: Z, ou ±HH:MM / ±HHMM
    let mut off_min: i64 = 0;
    if i < b.len() {
        match b[i] {
            b'Z' | b'z' => {}
            b'+' | b'-' => {
                let sign = if b[i] == b'-' { -1 } else { 1 };
                let oh = num(i + 1, 2)?;
                let om = if i + 5 <= b.len() && b[i + 3] == b':' { num(i + 4, 2)? }
                         else if i + 5 <= b.len() { num(i + 3, 2)? } else { 0 };
                off_min = sign * (oh * 60 + om);
            }
            _ => {}
        }
    }

    let days = days_from_civil(year, month, day);
    // segundos UTC = local - offset
    let local_secs = days * 86400 + hh * 3600 + mm * 60 + ss;
    let utc_secs = local_secs - off_min * 60;
    let epoch_secs = utc_secs as f64 + frac;

    // hour/dow em UTC
    let utc_days = utc_secs.div_euclid(86400);
    let sod = utc_secs.rem_euclid(86400);
    let hour = (sod / 3600) as i32;
    // weekday: 1970-01-01 = quinta; Hinnant weekday 0=domingo
    let wd0sun = (utc_days.rem_euclid(7) + 4).rem_euclid(7); // 0=domingo
    let dow_mon0 = ((wd0sun + 6) % 7) as i32; // seg=0..dom=6

    Some(Instant { epoch_secs, hour, dow_mon0 })
}

// ---- payload ----

#[derive(serde::Deserialize, Default)]
pub struct Payload {
    #[serde(default)]
    transaction: Transaction,
    #[serde(default)]
    customer: Customer,
    #[serde(default)]
    merchant: Merchant,
    #[serde(default)]
    terminal: Terminal,
    #[serde(default)]
    last_transaction: Option<LastTx>,
}

#[derive(serde::Deserialize, Default)]
struct Transaction {
    #[serde(default)] amount: f64,
    #[serde(default)] installments: f32,
    #[serde(default)] requested_at: String,
}
#[derive(serde::Deserialize, Default)]
struct Customer {
    #[serde(default)] avg_amount: f64,
    #[serde(default)] tx_count_24h: f32,
    #[serde(default)] known_merchants: Vec<String>,
}
#[derive(serde::Deserialize, Default)]
struct Merchant {
    #[serde(default)] id: String,
    #[serde(default)] mcc: Option<String>,
    #[serde(default)] avg_amount: f64,
}
#[derive(serde::Deserialize, Default)]
struct Terminal {
    #[serde(default)] is_online: bool,
    #[serde(default)] card_present: bool,
    #[serde(default)] km_from_home: f64,
}
#[derive(serde::Deserialize)]
struct LastTx {
    #[serde(default)] timestamp: String,
    #[serde(default)] km_from_current: f64,
}

// Vetoriza o payload UTF-8 nas 14 dims. None em erro (=> fallback 200 no chamador).
pub fn vectorize(utf8: &[u8], out: &mut [f32; DIMS]) -> Option<()> {
    let p: Payload = serde_json::from_slice(utf8).ok()?;
    let req = parse_instant(&p.transaction.requested_at)?;

    out[0] = clamp01((p.transaction.amount / MAX_AMOUNT) as f32);
    out[1] = clamp01(p.transaction.installments / MAX_INSTALLMENTS);
    let denom = p.customer.avg_amount * AMOUNT_VS_AVG_RATIO;
    out[2] = if denom > 0.0 { clamp01((p.transaction.amount / denom) as f32) } else { 0.0 };
    out[3] = req.hour as f32 / 23.0;
    out[4] = req.dow_mon0 as f32 / 6.0;

    match &p.last_transaction {
        Some(lt) => {
            let last = parse_instant(&lt.timestamp)?;
            let minutes = (req.epoch_secs - last.epoch_secs) / 60.0;
            out[5] = clamp01((minutes / MAX_MINUTES) as f32);
            out[6] = clamp01((lt.km_from_current / MAX_KM) as f32);
        }
        None => { out[5] = -1.0; out[6] = -1.0; }
    }

    out[7] = clamp01((p.terminal.km_from_home / MAX_KM) as f32);
    out[8] = clamp01(p.customer.tx_count_24h / MAX_TXCOUNT24H);
    out[9] = if p.terminal.is_online { 1.0 } else { 0.0 };
    out[10] = if p.terminal.card_present { 1.0 } else { 0.0 };
    let known = p.customer.known_merchants.iter().any(|m| m == &p.merchant.id);
    out[11] = if known { 0.0 } else { 1.0 };
    out[12] = mcc_risk(p.merchant.mcc.as_deref());
    out[13] = clamp01((p.merchant.avg_amount / MAX_MERCHANT_AVG) as f32);
    Some(())
}

#[inline]
pub fn quantize_i16(x: f32) -> i16 {
    let q = (x * SCALE16).round() as i32;
    q.clamp(i16::MIN as i32, i16::MAX as i32) as i16
}
