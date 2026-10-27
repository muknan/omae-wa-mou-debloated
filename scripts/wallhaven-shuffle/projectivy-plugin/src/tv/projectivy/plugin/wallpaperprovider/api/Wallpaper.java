package tv.projectivy.plugin.wallpaperprovider.api;

import android.os.Parcel;
import android.os.Parcelable;

/**
 * Projectivy's Wallpaper (projectivy-plugin-wallpaper-provider, Apache 2.0). Written field by field in the order of
 * the Kotlin data class, exactly as its @Parcelize code does: uri, type, displayMode, title, source, author,
 * actionUri (strings with writeString, which also covers null).
 */
public final class Wallpaper implements Parcelable {
    public static final int TYPE_IMAGE = 0;
    public static final int MODE_DEFAULT = 0, MODE_CROP = 1;

    public final String uri, title, source, author, actionUri;
    public final int type, displayMode;

    public Wallpaper(String uri, int type, int displayMode, String title, String source, String author, String actionUri) {
        this.uri = uri; this.type = type; this.displayMode = displayMode;
        this.title = title; this.source = source; this.author = author; this.actionUri = actionUri;
    }

    @Override public int describeContents() { return 0; }

    @Override public void writeToParcel(Parcel out, int flags) {
        out.writeString(uri);
        out.writeInt(type);
        out.writeInt(displayMode);
        out.writeString(title);
        out.writeString(source);
        out.writeString(author);
        out.writeString(actionUri);
    }

    public static final Creator<Wallpaper> CREATOR = new Creator<Wallpaper>() {
        @Override public Wallpaper createFromParcel(Parcel in) {
            return new Wallpaper(in.readString(), in.readInt(), in.readInt(), in.readString(), in.readString(), in.readString(), in.readString());
        }
        @Override public Wallpaper[] newArray(int size) { return new Wallpaper[size]; }
    };
}
