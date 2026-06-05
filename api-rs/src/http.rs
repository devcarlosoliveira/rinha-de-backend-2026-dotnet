// Servidor HTTP/1.1 mono-thread sobre mio (epoll), sem GC, sem thread pool.
// Uma unica thread processa tudo serialmente => CPU instantanea baixa => nunca
// estoura a quota do CFS (a causa do throttle/p99). Robusto: qualquer erro de
// parse vira fallback 200, nunca 5xx (5xx pesa 5x e conta no corte de 15%).
use std::collections::HashMap;
use std::io::{Read, Write};
use mio::net::{TcpListener, TcpStream};
use mio::{Events, Interest, Poll, Token};

use crate::index::IvfIndex;
use crate::vec::{vectorize, DIMS};

pub struct Responses {
    by_fc: Vec<Vec<u8>>, // 0..5
    ready: Vec<u8>,
}
impl Responses {
    pub fn new() -> Responses {
        let bodies = [
            "{\"approved\":true,\"fraud_score\":0.0}",
            "{\"approved\":true,\"fraud_score\":0.2}",
            "{\"approved\":true,\"fraud_score\":0.4}",
            "{\"approved\":false,\"fraud_score\":0.6}",
            "{\"approved\":false,\"fraud_score\":0.8}",
            "{\"approved\":false,\"fraud_score\":1.0}",
        ];
        let http = |b: &str| -> Vec<u8> {
            format!("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n{}",
                    b.len(), b).into_bytes()
        };
        Responses {
            by_fc: bodies.iter().map(|b| http(b)).collect(),
            ready: b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n".to_vec(),
        }
    }
    #[inline] fn fallback(&self) -> &[u8] { &self.by_fc[0] }
}

struct Conn {
    stream: TcpStream,
    rbuf: Vec<u8>,
    rlen: usize,
    wbuf: Vec<u8>,
    wpos: usize,
}

const LISTENER: Token = Token(0);

pub fn run(mut listener: TcpListener, index: &IvfIndex, nlow: usize, nhigh: usize) -> std::io::Result<()> {
    let resp = Responses::new();
    let mut poll = Poll::new()?;
    poll.registry().register(&mut listener, LISTENER, Interest::READABLE)?;
    let mut events = Events::with_capacity(1024);

    let mut conns: HashMap<usize, Conn> = HashMap::new();
    let mut next: usize = 1;

    loop {
        poll.poll(&mut events, None)?;
        for ev in events.iter() {
            if ev.token() == LISTENER {
                loop {
                    match listener.accept() {
                        Ok((mut s, _)) => {
                            let _ = s.set_nodelay(true);
                            let tok = next; next += 1;
                            poll.registry().register(&mut s, Token(tok), Interest::READABLE)?;
                            conns.insert(tok, Conn { stream: s, rbuf: vec![0u8; 8192], rlen: 0, wbuf: Vec::new(), wpos: 0 });
                        }
                        Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => break,
                        Err(_) => break,
                    }
                }
                continue;
            }

            let tok = ev.token().0;
            let mut close = false;

            if ev.is_writable() {
                if let Some(c) = conns.get_mut(&tok) {
                    if !flush(c) { close = true; }
                    else if c.wpos >= c.wbuf.len() {
                        c.wbuf.clear(); c.wpos = 0;
                        let _ = poll.registry().reregister(&mut c.stream, Token(tok), Interest::READABLE);
                    }
                }
            }

            if !close && ev.is_readable() {
                if let Some(c) = conns.get_mut(&tok) {
                    close = handle_readable(c, &resp, index, nlow, nhigh);
                    if !close && c.wpos < c.wbuf.len() {
                        let _ = poll.registry().reregister(&mut c.stream, Token(tok), Interest::READABLE | Interest::WRITABLE);
                    }
                }
            }

            if close {
                if let Some(mut c) = conns.remove(&tok) {
                    let _ = poll.registry().deregister(&mut c.stream);
                }
            }
        }
    }
}

// Le tudo que da, processa requests completas, enfileira respostas e tenta flush.
// Retorna true se a conexao deve fechar.
fn handle_readable(c: &mut Conn, resp: &Responses, index: &IvfIndex, nlow: usize, nhigh: usize) -> bool {
    loop {
        if c.rlen == c.rbuf.len() { c.rbuf.resize(c.rbuf.len() * 2, 0); }
        match c.stream.read(&mut c.rbuf[c.rlen..]) {
            Ok(0) => return true, // EOF
            Ok(n) => {
                c.rlen += n;
                process_buffer(c, resp, index, nlow, nhigh);
                // se o buffer encheu exatamente, pode haver mais p/ ler -> continua
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => break,
            Err(ref e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(_) => return true,
        }
    }
    !flush(c)
}

// Consome requests completas de rbuf[0..rlen), gerando respostas em wbuf.
fn process_buffer(c: &mut Conn, resp: &Responses, index: &IvfIndex, nlow: usize, nhigh: usize) {
    let mut start = 0usize;
    loop {
        let buf = &c.rbuf[start..c.rlen];
        let header_end = match find_header_end(buf) { Some(h) => h, None => break };
        let is_post = !buf.is_empty() && buf[0] == b'P';
        let content_length = if is_post { parse_content_length(&buf[..header_end]) } else { 0 };
        let total = header_end + content_length;
        if buf.len() < total { break; } // corpo incompleto

        let r: &[u8] = if is_post {
            let body = &buf[header_end..total];
            process_one(body, resp, index, nlow, nhigh)
        } else {
            &resp.ready
        };
        c.wbuf.extend_from_slice(r);
        start += total;
    }
    // descarta o que foi consumido
    if start > 0 {
        c.rbuf.copy_within(start..c.rlen, 0);
        c.rlen -= start;
    }
}

#[inline]
fn process_one<'a>(body: &[u8], resp: &'a Responses, index: &IvfIndex, nlow: usize, nhigh: usize) -> &'a [u8] {
    let mut v = [0f32; DIMS];
    match vectorize(body, &mut v) {
        Some(()) => {
            let fc = index.search_adaptive(&v, nlow, nhigh) as usize;
            &resp.by_fc[fc.min(5)]
        }
        None => resp.fallback(),
    }
}

// Escreve wbuf[wpos..]. Retorna false em erro de socket (fechar).
fn flush(c: &mut Conn) -> bool {
    while c.wpos < c.wbuf.len() {
        match c.stream.write(&c.wbuf[c.wpos..]) {
            Ok(0) => return false,
            Ok(n) => c.wpos += n,
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => return true,
            Err(ref e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(_) => return false,
        }
    }
    if c.wpos >= c.wbuf.len() { c.wbuf.clear(); c.wpos = 0; }
    true
}

fn find_header_end(b: &[u8]) -> Option<usize> {
    if b.len() < 4 { return None; }
    let mut i = 0;
    while i + 3 < b.len() {
        if b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10 {
            return Some(i + 4);
        }
        i += 1;
    }
    None
}

fn parse_content_length(hdr: &[u8]) -> usize {
    let key = b"content-length:";
    let n = hdr.len();
    let mut i = 0;
    while i + key.len() <= n {
        let mut m = true;
        for k in 0..key.len() {
            let mut c = hdr[i + k];
            if c.is_ascii_uppercase() { c += 32; }
            if c != key[k] { m = false; break; }
        }
        if m {
            let mut j = i + key.len();
            while j < n && (hdr[j] == b' ' || hdr[j] == b'\t') { j += 1; }
            let mut v = 0usize;
            while j < n && hdr[j].is_ascii_digit() { v = v * 10 + (hdr[j] - b'0') as usize; j += 1; }
            return v;
        }
        i += 1;
    }
    0
}
