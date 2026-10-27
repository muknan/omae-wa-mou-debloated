package tv.projectivy.plugin.wallpaperprovider.api;

import android.os.Parcel;
import android.os.Parcelable;

/**
 * Projectivy's Event, as it arrives over Binder (projectivy-plugin-wallpaper-provider, Apache 2.0). Every event's
 * parcel starts with its type; this plugin only needs the type (it subscribes to TIME_ELAPSED alone), so the rest of
 * an event's fields are left unread.
 */
public final class Event implements Parcelable {
    public static final int TIME_ELAPSED = 1, NOW_PLAYING_CHANGED = 2, CARD_FOCUSED = 4, PROGRAM_CARD_FOCUSED = 8,
        LAUNCHER_IDLE_MODE_CHANGED = 16;

    public final int eventType;

    public Event(int eventType) { this.eventType = eventType; }

    @Override public int describeContents() { return 0; }
    @Override public void writeToParcel(Parcel out, int flags) { out.writeInt(eventType); }

    public static final Creator<Event> CREATOR = new Creator<Event>() {
        @Override public Event createFromParcel(Parcel in) { return new Event(in.readInt()); }
        @Override public Event[] newArray(int size) { return new Event[size]; }
    };
}
