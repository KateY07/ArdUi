"""Build an isolated ARD copy-buffer experiment; never replace a production binary."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess


def replace_once(text, before, after):
    if text.count(before) != 1:
        raise RuntimeError(f"Expected exactly one source anchor: {before[:100]!r}")
    return text.replace(before, after, 1)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-only", action="store_true")
    args = parser.parse_args()
    ard_ui = Path(__file__).resolve().parents[2]
    repo = ard_ui.parent
    output = ard_ui / "dist" / "optimization-round1"
    source = output / "ard-source"
    variants = output / "ard-variants"
    source.mkdir(parents=True, exist_ok=True)
    variants.mkdir(parents=True, exist_ok=True)
    protected = [repo / "src" / "main.rs", repo / "Cargo.toml", repo / "Cargo.lock",
                 repo / "README.md", repo / "target" / "release" / "ard.exe"]
    before_hashes = {str(path): digest(path) for path in protected}
    shutil.copytree(repo / "src", source / "src", dirs_exist_ok=True)
    shutil.copy2(repo / "Cargo.lock", source / "Cargo.lock")
    manifest = (repo / "Cargo.toml").read_text(encoding="utf-8")
    manifest = replace_once(manifest, 'members = ["relay"]', 'members = []')
    manifest = replace_once(manifest, '[dependencies]', '[[bin]]\nname = "ard-perf"\npath = "src/main.rs"\n\n[dependencies]')
    (source / "Cargo.toml").write_text(manifest, encoding="utf-8")
    code = (repo / "src" / "main.rs").read_text(encoding="utf-8")
    code = replace_once(code, 'const ALPN: &[u8] = b"ard/2";', '''// Isolated experiment configuration; this file is generated from production source.
static BENCH_COPY_BYTES: AtomicUsize = AtomicUsize::new(8 * 1024);
static BENCH_WINDOW_BYTES: AtomicU64 = AtomicU64::new(0);

fn configure_benchmark() -> Result<()> {
    let copy_kib = env::var("ARD_BENCH_COPY_KIB")
        .unwrap_or_else(|_| "8".to_owned())
        .parse::<usize>().context("parse ARD_BENCH_COPY_KIB")?;
    ensure!([0, 8, 32, 64, 128].contains(&copy_kib), "copy KiB must be 0, 8, 32, 64 or 128");
    let window = env::var("ARD_BENCH_WINDOW_BYTES")
        .unwrap_or_else(|_| "0".to_owned())
        .parse::<u64>().context("parse ARD_BENCH_WINDOW_BYTES")?;
    ensure!(window <= 256 * 1024 * 1024, "experimental window must be <= 256 MiB");
    BENCH_COPY_BYTES.store(copy_kib * 1024, Ordering::Relaxed);
    BENCH_WINDOW_BYTES.store(window, Ordering::Relaxed);
    eprintln!("ARD_BENCH_CONFIG copy_kib={copy_kib} window_bytes={window} (copy=0/window=0 retains production defaults)");
    Ok(())
}

async fn bench_copy<R, W>(reader: &mut R, writer: &mut W) -> io::Result<u64>
where
    R: tokio::io::AsyncRead + Unpin,
    W: tokio::io::AsyncWrite + Unpin,
{
    let capacity = BENCH_COPY_BYTES.load(Ordering::Relaxed);
    if capacity == 0 {
        return tokio::io::copy(reader, writer).await;
    }
    // BufReader returns currently available bytes, without waiting to fill capacity.
    let mut buffered = tokio::io::BufReader::with_capacity(capacity, reader);
    tokio::io::copy_buf(&mut buffered, writer).await
}

const ALPN: &[u8] = b"ard/2";''')
    code = replace_once(code, '    info!(version = env!("CARGO_PKG_VERSION"), "ARD starting");', '''    if let Err(error) = configure_benchmark() {
        eprintln!("ard-perf: invalid benchmark configuration: {error:#}");
        return ExitCode::FAILURE;
    }
    info!(version = env!("CARGO_PKG_VERSION"), "ARD starting");''')
    code = replace_once(code, '    let transport = QuicTransportConfig::builder()', '    let mut transport = QuicTransportConfig::builder()')
    code = replace_once(code, '''        .datagram_send_buffer_size(QUIC_DATAGRAM_BUFFER_SIZE)
        .build();''', '''        .datagram_send_buffer_size(QUIC_DATAGRAM_BUFFER_SIZE);
    let window = BENCH_WINDOW_BYTES.load(Ordering::Relaxed);
    if window != 0 {
        let value = VarInt::from_u64(window).context("experimental QUIC window")?;
        transport = transport.stream_receive_window(value).receive_window(value).send_window(window);
    }
    let transport = transport.build();''')
    code = replace_once(code, 'tokio::io::copy(&mut tcp_read, &mut send)', 'bench_copy(&mut tcp_read, &mut send)')
    code = replace_once(code, 'tokio::io::copy(&mut recv, &mut tcp_write)', 'bench_copy(&mut recv, &mut tcp_write)')
    code = code.replace('ard=debug', 'ard_perf=debug').replace('ard=trace', 'ard_perf=trace').replace('ard=info', 'ard_perf=info')
    (source / "src" / "main.rs").write_text(code, encoding="utf-8")
    if args.prepare_only:
        print(source)
        return
    cargo = Path.home() / ".cargo" / "bin" / "cargo.exe"
    environment = dict(os.environ, CARGO_TARGET_DIR=str(repo / "target"))
    command = [str(cargo), "build", "--offline", "--release", "--manifest-path",
               str(source / "Cargo.toml"), "--bin", "ard-perf"]
    subprocess.run(command, cwd=repo, env=environment, check=True)
    binary = variants / "ard-perf.exe"
    shutil.copy2(repo / "target" / "release" / "ard-perf.exe", binary)
    after_hashes = {str(path): digest(path) for path in protected}
    if before_hashes != after_hashes:
        raise RuntimeError("A protected production file changed during preparation")
    metadata = {
        "binary": str(binary), "sha256": digest(binary), "build_command": command,
        "production_files_unchanged": before_hashes,
        "copy_kib": {"allowed": [0, 8, 32, 64, 128], "default": 8,
                     "0": "original tokio::io::copy", "nonzero": "BufReader + copy_buf"},
        "window_bytes": {"default": 0, "0": "original transport defaults",
                         "nonzero": "sets stream/connection receive and send windows; <=256MiB"},
    }
    (variants / "build.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(json.dumps(metadata, indent=2))


if __name__ == "__main__":
    main()
