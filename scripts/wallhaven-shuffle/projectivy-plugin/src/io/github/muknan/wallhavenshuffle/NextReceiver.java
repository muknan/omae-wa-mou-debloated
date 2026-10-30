package io.github.muknan.wallhavenshuffle;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/**
 * "Next wallpaper" from anywhere, e.g. a remote button through Key Mapper (Shell command, ADB mode):
 *   am broadcast -a io.github.muknan.wallhavenshuffle.NEXT -n io.github.muknan.wallhavenshuffle/.NextReceiver
 * It asks Projectivy to refresh this plugin's wallpapers; Projectivy then asks for one and gets the next (right away,
 * whatever this TV's "Change" interval).
 */
public final class NextReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context c, Intent intent) {
        ShuffleService.skipWait(c);
    }
}
