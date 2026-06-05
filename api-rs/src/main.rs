mod vec;
mod index;
mod http;

use std::sync::Arc;
use index::IvfIndex;
use mio::net::TcpListener;
use socket2::{Domain, Protocol, Socket, Type};

fn env_usize(name: &str, def: usize) -> usize {
    std::env::var(name).ok().and_then(|s| s.parse().ok()).unwrap_or(def)
}

// Listener com SO_REUSEPORT: N threads bindam a mesma porta e o kernel distribui as
// conexoes entre elas (N event loops paralelos, sem GC).
fn reuseport_listener(port: u16) -> std::io::Result<TcpListener> {
    let sock = Socket::new(Domain::IPV4, Type::STREAM, Some(Protocol::TCP))?;
    sock.set_reuse_address(true)?;
    sock.set_reuse_port(true)?;
    sock.set_nonblocking(true)?;
    let addr: std::net::SocketAddr = format!("0.0.0.0:{port}").parse().unwrap();
    sock.bind(&addr.into())?;
    sock.listen(1024)?;
    Ok(TcpListener::from_std(sock.into()))
}

fn main() {
    let index_path = std::env::var("INDEX_PATH").unwrap_or_else(|_| "artifacts/index.bin".into());
    let port = env_usize("PORT", 9999) as u16;
    let nlow = env_usize("NLOW", 8);
    let nhigh = env_usize("NHIGH", 128);
    let threads = env_usize("THREADS", 4).max(1);

    eprintln!("carregando {index_path} (adaptativo nLow={nlow} nHigh={nhigh}, threads={threads})...");
    let index = Arc::new(IvfIndex::load(&index_path).expect("falha ao carregar index.bin"));
    eprintln!("indice pronto: N={} K={} avx2={} — escutando :{port}",
              index.n, index.k, is_x86_feature_detected!("avx2"));

    let mut handles = Vec::new();
    for _ in 0..threads {
        let idx = index.clone();
        handles.push(std::thread::spawn(move || {
            let listener = reuseport_listener(port).expect("bind falhou");
            http::run(listener, &idx, nlow, nhigh).expect("event loop falhou");
        }));
    }
    for h in handles { let _ = h.join(); }
}
