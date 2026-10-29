package io.github.muknan.wallhavenshuffle;

import android.app.Activity;
import android.graphics.ImageFormat;
import android.media.Image;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaExtractor;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.KeyEvent;
import android.view.SurfaceView;
import android.widget.FrameLayout;

import java.io.File;
import java.nio.ByteBuffer;

/**
 * Puts the TV panel back to 60 Hz. On MediaTek TVs (the Xiaomi test TV) the video chip retimes the panel to each
 * video's frame rate ("frame sync"): 25 fps runs the panel at 50 Hz, which is right for the video. But when the video
 * ends nothing switches it back, so the home screen keeps running at 50 Hz while Android draws for 60, and judders
 * until a restart. A video at 24 fps makes the chip pick 60 Hz again, and it stays there after the video ends.
 *
 * This plays a silent black 24 fps clip of under a second in a tiny corner of a see-through screen, then closes. It's
 * made once on the TV and kept. tv-daily.sh starts it when the home screen comes back at 50 Hz:
 *   am start -n io.github.muknan.wallhavenshuffle/.PanelResetActivity [--ei fps 25] [--ei size 2] [--ei ms 750]
 * (For testing: fps 25 does the opposite; size is the video's width and height on screen, 0 = full screen; ms is how
 * long it plays, up to 20 s.)
 */
public final class PanelResetActivity extends Activity implements SurfaceHolder.Callback {
    private static final int W = 320, H = 180;
    private volatile boolean started;

    @Override protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        int size = getIntent().getIntExtra("size", 2);
        int px = size <= 0 ? FrameLayout.LayoutParams.MATCH_PARENT : size;
        FrameLayout root = new FrameLayout(this);
        SurfaceView view = new SurfaceView(this);
        root.addView(view, new FrameLayout.LayoutParams(px, px));
        setContentView(root);
        view.getHolder().addCallback(this);
        root.postDelayed(this::finish, ms() + 4000);                 // never stay, whatever happens
    }

    @Override public void surfaceCreated(SurfaceHolder holder) {
        if (started) return;
        started = true;
        int fps = Math.max(1, Math.min(60, getIntent().getIntExtra("fps", 24)));
        Surface surface = holder.getSurface();
        new Thread(() -> {
            try {
                play(clip(fps, ms()), surface, fps);
            } catch (Exception e) {
                Log.w(ListStore.TAG, "panel reset failed: " + e);
            }
            runOnUiThread(this::finish);
        }, "panel-reset").start();
    }

    @Override public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) { }
    @Override public void surfaceDestroyed(SurfaceHolder holder) { }

    // A remote press while it plays (under a second): close at once, so the home screen has the remote back. The panel
    // switches as soon as the clip starts; if it was too early, tv-daily.sh sees that and tries once more.
    @Override public boolean onKeyDown(int keyCode, KeyEvent event) {
        finish();
        return true;
    }

    @Override protected void onPause() {
        super.onPause();
        finish();                                                    // leaving (Home, another app): just go
    }

    private int ms() { return Math.max(250, Math.min(20_000, getIntent().getIntExtra("ms", 750))); }

    /** The clip for this frame rate and length: made on first use (a few KB of black H.264), then reused. */
    private File clip(int fps, int ms) throws Exception {
        int frames = Math.max(6, fps * ms / 1000);
        File f = new File(getCacheDir(), "black-" + fps + "fps-" + frames + ".mp4");
        if (f.length() > 0) return f;
        File tmp = new File(getCacheDir(), f.getName() + ".tmp");
        MediaFormat fmt = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, W, H);
        fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);
        fmt.setInteger(MediaFormat.KEY_BIT_RATE, 100_000);
        fmt.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
        MediaCodec enc = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC);
        MediaMuxer mux = new MediaMuxer(tmp.getPath(), MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);
        try {
            enc.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            enc.start();
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            int track = -1, sent = 0;
            boolean done = false;
            while (!done) {
                if (sent <= frames) {
                    int in = enc.dequeueInputBuffer(10_000);
                    if (in >= 0) {
                        if (sent == frames) {
                            enc.queueInputBuffer(in, 0, 0, sent * 1_000_000L / fps, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                        } else {
                            Image img = enc.getInputImage(in);
                            fillBlack(img);
                            enc.queueInputBuffer(in, 0, W * H * 3 / 2, sent * 1_000_000L / fps, 0);
                        }
                        sent++;
                    }
                }
                int out = enc.dequeueOutputBuffer(info, 10_000);
                if (out == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    track = mux.addTrack(enc.getOutputFormat());
                    mux.start();
                } else if (out >= 0) {
                    ByteBuffer b = enc.getOutputBuffer(out);
                    boolean config = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                    if (!config && info.size > 0 && track >= 0) mux.writeSampleData(track, b, info);
                    enc.releaseOutputBuffer(out, false);
                    done = (info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                }
            }
        } finally {
            try { enc.stop(); } catch (Exception ignored) { }
            enc.release();
            try { mux.stop(); } catch (Exception ignored) { }
            mux.release();
        }
        if (!tmp.renameTo(f)) throw new IllegalStateException("couldn't save " + f.getName());
        return f;
    }

    private static void fillBlack(Image img) {
        if (img == null || img.getFormat() != ImageFormat.YUV_420_888) return;
        Image.Plane[] p = img.getPlanes();
        for (int i = 0; i < 3; i++) {
            ByteBuffer b = p[i].getBuffer();
            byte v = (byte) (i == 0 ? 16 : 128);                     // black: Y 16, U and V 128
            while (b.hasRemaining()) b.put(v);
        }
    }

    /** Decodes the clip onto the surface at its own pace, with the frame rate stated to the decoder and the display. */
    private static void play(File clip, Surface surface, int fps) throws Exception {
        if (Build.VERSION.SDK_INT >= 30) surface.setFrameRate(fps, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE);
        MediaExtractor ex = new MediaExtractor();
        MediaCodec dec = null;
        try {
            ex.setDataSource(clip.getPath());
            ex.selectTrack(0);
            MediaFormat fmt = ex.getTrackFormat(0);
            fmt.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
            dec = MediaCodec.createDecoderByType(fmt.getString(MediaFormat.KEY_MIME));
            dec.configure(fmt, surface, null, 0);
            dec.start();
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            long t0 = System.nanoTime();
            boolean inputDone = false, done = false;
            while (!done) {
                if (!inputDone) {
                    int in = dec.dequeueInputBuffer(10_000);
                    if (in >= 0) {
                        int n = ex.readSampleData(dec.getInputBuffer(in), 0);
                        if (n < 0) {
                            dec.queueInputBuffer(in, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                            inputDone = true;
                        } else {
                            dec.queueInputBuffer(in, 0, n, ex.getSampleTime(), 0);
                            ex.advance();
                        }
                    }
                }
                int out = dec.dequeueOutputBuffer(info, 10_000);
                if (out >= 0) {
                    long wait = (t0 + info.presentationTimeUs * 1000) - System.nanoTime();
                    if (wait > 0) Thread.sleep(wait / 1_000_000, (int) (wait % 1_000_000));
                    dec.releaseOutputBuffer(out, info.size > 0);
                    done = (info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                }
            }
        } finally {
            if (dec != null) { try { dec.stop(); } catch (Exception ignored) { } dec.release(); }
            ex.release();
        }
    }
}
