mod vec;
mod index;
mod http;

use index::IvfIndex;

fn env_usize(name: &str, def: usize) -> usize {
    std::env::var(name).ok().and_then(|s| s.parse().ok()).unwrap_or(def)
}

fn main() {
    let index_path = std::env::var("INDEX_PATH").unwrap_or_else(|_| "artifacts/index.bin".into());
    let port = env_usize("PORT", 9999) as u16;
    let nlow = env_usize("NLOW", 8);
    let nhigh = env_usize("NHIGH", 128);

    eprintln!("carregando {index_path} (adaptativo nLow={nlow} nHigh={nhigh})...");
    let index = IvfIndex::load(&index_path).expect("falha ao carregar index.bin");
    eprintln!("indice pronto: N={} K={} avx2={} — escutando :{port}",
              index.n, index.k, is_x86_feature_detected!("avx2"));

    http::run(port, &index, nlow, nhigh).expect("servidor falhou");
}
