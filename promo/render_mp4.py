"""Renders ardui-promo.html to MP4: frames from headless Edge (deterministic, one renderAt(t) per frame),
audio from the page's own synth through an OfflineAudioContext, muxed with the ffmpeg bundled in imageio-ffmpeg.

    python render_mp4.py [--fps 30] [--out ardui-promo.mp4] [--seconds N]   (N: render only the first N seconds, for a test)
"""
import argparse, base64, os, subprocess, sys, time
from pathlib import Path
import imageio_ffmpeg
from playwright.sync_api import sync_playwright

here = Path(__file__).resolve().parent
ap = argparse.ArgumentParser()
ap.add_argument('--fps', type=int, default=30)
ap.add_argument('--out', default=str(here / 'ardui-promo.mp4'))
ap.add_argument('--seconds', type=float, default=0)
args = ap.parse_args()

AUDIO_JS = """
async () => {
  const sr = 44100;
  const off = new OfflineAudioContext(2, Math.ceil(sr * (TOTAL + 1.5)), sr);
  ctx = off; NB = noiseBuf(); audioOn = true;
  master = off.createGain(); master.gain.value = .8;
  const comp = off.createDynamicsCompressor(); master.connect(comp); comp.connect(off.destination);
  schedule(0, 0);
  const buf = await off.startRendering();
  // 16-bit PCM WAV
  const n = buf.length, ch = buf.numberOfChannels, out = new DataView(new ArrayBuffer(44 + n * ch * 2));
  const w = (o, s) => [...s].forEach((c, i) => out.setUint8(o + i, c.charCodeAt(0)));
  w(0, 'RIFF'); out.setUint32(4, 36 + n * ch * 2, true); w(8, 'WAVEfmt '); out.setUint32(16, 16, true); out.setUint16(20, 1, true); out.setUint16(22, ch, true);
  out.setUint32(24, sr, true); out.setUint32(28, sr * ch * 2, true); out.setUint16(32, ch * 2, true); out.setUint16(34, 16, true); w(36, 'data'); out.setUint32(40, n * ch * 2, true);
  const data = [...Array(ch).keys()].map(c => buf.getChannelData(c));
  let o = 44; for (let i = 0; i < n; i++) for (let c = 0; c < ch; c++) { const v = Math.max(-1, Math.min(1, data[c][i])); out.setInt16(o, v < 0 ? v * 0x8000 : v * 0x7fff, true); o += 2; }
  let bin = ''; const bytes = new Uint8Array(out.buffer); const step = 0x8000;
  for (let i = 0; i < bytes.length; i += step) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
  return btoa(bin);
}
"""

with sync_playwright() as p:
    browser = p.chromium.launch(channel='msedge', headless=True, args=['--autoplay-policy=no-user-gesture-required', '--force-color-profile=srgb'])
    page = browser.new_page(viewport={'width': 1920, 'height': 1080}, device_scale_factor=1)
    page.goto((here / 'ardui-promo.html').as_uri())
    page.evaluate("document.getElementById('start').remove(); document.getElementById('ui').style.display = 'none'; playing = false; fit();")
    total = page.evaluate('TOTAL')
    seconds = min(total, args.seconds) if args.seconds else total
    wav_path = here / '_audio.wav'
    print('rendering audio...', flush=True)
    wav_path.write_bytes(base64.b64decode(page.evaluate(AUDIO_JS)))
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    frames = int(round(seconds * args.fps))
    cmd = [ffmpeg, '-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', str(args.fps), '-c:v', 'mjpeg', '-i', '-', '-i', str(wav_path),
           '-t', f'{seconds:.3f}', '-c:v', 'libx264', '-preset', 'slow', '-crf', '15', '-pix_fmt', 'yuv420p', '-profile:v', 'high',
           '-c:a', 'aac', '-b:a', '192k', '-af', 'afade=t=out:st=%.3f:d=1.2' % max(0, seconds - 1.2), '-movflags', '+faststart', args.out]
    enc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    client = page.context.new_cdp_session(page)
    started = time.time()
    for i in range(frames):
        t = i / args.fps
        page.evaluate(f'pos = {t}; renderAt({t});')
        shot = client.send('Page.captureScreenshot', {'format': 'jpeg', 'quality': 96, 'fromSurface': True})
        enc.stdin.write(base64.b64decode(shot['data']))
        if i % 60 == 0:
            print(f'frame {i}/{frames}  {time.time() - started:.0f}s', flush=True)
    enc.stdin.close(); enc.wait()
    browser.close()
wav_path.unlink(missing_ok=True)
print('done', args.out, f'{os.path.getsize(args.out) / 1e6:.1f} MB', flush=True)
