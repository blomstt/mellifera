using System.Runtime.InteropServices;

namespace Hive.Gui;

// The few GLib/GTK calls the tests need that the window itself doesn't make:
// pumping the main loop, walking the widget tree and reading widget state.
// Kept apart from src/Gui/Native.cs (class N) so the window's bindings stay
// exactly what the window uses.
static unsafe partial class G
{
    const string GObject = "libgobject-2.0.so.0", GLib = "libglib-2.0.so.0", Gio = "libgio-2.0.so.0";
    const string Gtk = "libgtk-4.so.1", Adw = "libadwaita-1.so.0";

    [LibraryImport(GLib)] public static partial int g_main_context_iteration(nint context, int mayBlock);
    [LibraryImport(GLib)] public static partial int g_log_set_always_fatal(int fatalMask);
    [LibraryImport(GObject)] public static partial void g_object_add_weak_pointer(nint obj, nint location);
    [LibraryImport(GObject)] public static partial nint g_type_name_from_instance(nint instance);
    [LibraryImport(Gio)] public static partial uint g_list_model_get_n_items(nint model);

    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_first_child(nint widget);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_next_sibling(nint widget);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_parent(nint widget);
    [LibraryImport(Gtk)] public static partial int gtk_widget_activate(nint widget);
    [LibraryImport(Gtk)] public static partial int gtk_widget_get_visible(nint widget);
    [LibraryImport(Gtk)] public static partial nint gtk_button_get_label(nint button);
    [LibraryImport(Gtk)] public static partial nint gtk_drop_down_get_model(nint dropDown);
    [LibraryImport(Gtk)] public static partial nint gtk_string_list_get_string(nint list, uint position);
    [LibraryImport(Gtk)] public static partial double gtk_adjustment_get_upper(nint adjustment);
    [LibraryImport(Gtk)] public static partial double gtk_adjustment_get_page_size(nint adjustment);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_get_iter_at_offset(nint buffer, byte* iter, int offset);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_place_cursor(nint buffer, byte* iter);
    [LibraryImport(Gtk)] public static partial nint gtk_text_buffer_get_insert(nint buffer);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_get_iter_at_mark(nint buffer, byte* iter, nint mark);
    [LibraryImport(Gtk)] public static partial int gtk_text_iter_get_offset(byte* iter);
    [LibraryImport(Gtk)] public static partial int gtk_widget_compute_point(nint widget, nint target, float* point, float* outPoint);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_window_to_buffer_coords(nint view, int win, int windowX, int windowY, int* bufferX, int* bufferY);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_get_line_at_y(nint view, byte* iter, int y, int* lineTop);
    [LibraryImport(Gtk)] public static partial int gtk_text_iter_get_line(byte* iter);
    [LibraryImport(Adw)] public static partial uint adw_view_stack_page_get_badge_number(nint page);
    [LibraryImport(Gtk)] public static partial void gtk_widget_get_color(nint widget, float* rgba);
    [LibraryImport(Adw)] public static partial int adw_navigation_split_view_get_collapsed(nint split);
    [LibraryImport(Gtk)] public static partial int gtk_test_accessible_has_property(nint accessible, int property);
    // Varargs: one string value. NULL when the property has that value, else a message (leaked; it's a test).
    [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] public static partial nint gtk_test_accessible_check_property(nint accessible, int property, string value);
    public static bool Says(nint accessible, string label) => gtk_test_accessible_check_property(accessible, 4, label) == 0;
    // What a screen reader hears for it: GTK's check message, which quotes the label it has.
    public static string Heard(nint accessible)
    {
        const string sentinel = "\u0001";
        var m = gtk_test_accessible_check_property(accessible, 4, sentinel);
        return m == 0 ? sentinel : Marshal.PtrToStringUTF8(m) ?? "";
    }
    // Never heard by a screen reader: its accessible role is "none".
    [LibraryImport(Gtk)] public static partial int gtk_accessible_get_accessible_role(nint accessible);
    public static bool Unspoken(nint accessible) => gtk_accessible_get_accessible_role(accessible) == Ui.RoleNone;
    [LibraryImport(Gtk)] public static partial nint gtk_settings_get_default();
    [LibraryImport(Gtk)] public static partial double gtk_widget_get_opacity(nint widget);
    // Varargs: one property, then the NULL that ends the list.
    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)] public static partial void g_object_set(nint obj, string property, int value, nint end);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_tooltip_text(nint widget);
    [LibraryImport(Gtk)] public static partial nint gtk_label_get_text(nint label);
    [LibraryImport(Gtk)] public static partial nint gtk_entry_get_placeholder_text(nint entry);
    // Varargs: no arguments after the signal's name.
    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)] public static partial void g_signal_emit_by_name(nint instance, string signal, nint end);
    [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] public static partial int gtk_widget_has_css_class(nint widget, string css);

    [LibraryImport(Gtk)] public static partial void gtk_widget_set_state_flags(nint widget, int flags, int clear);
    [LibraryImport(Gtk)] public static partial nint gtk_scrolled_window_get_child(nint scrolledWindow);
    [LibraryImport(Gtk)] public static partial nint gtk_text_view_get_gutter(nint view, int window);
    [LibraryImport(Gtk)] public static partial nint gtk_text_buffer_get_tag_table(nint buffer);
    [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] public static partial nint gtk_text_tag_table_lookup(nint table, string name);
    [LibraryImport(Gtk)] public static partial int gtk_text_buffer_get_iter_at_line_offset(nint buffer, byte* iter, int line, int offset);
    [LibraryImport(Gtk)] public static partial int gtk_text_iter_has_tag(byte* iter, nint tag);

    // Whether the character at (line, offset) of a text view has the tag named so.
    public static bool HasTag(nint view, int line, int offset, string tag)
    {
        var buffer = N.gtk_text_view_get_buffer(view);
        var t = gtk_text_tag_table_lookup(gtk_text_buffer_get_tag_table(buffer), tag);
        if (t == 0) return false;
        byte* iter = stackalloc byte[256];
        gtk_text_buffer_get_iter_at_line_offset(buffer, iter, line, offset);
        return gtk_text_iter_has_tag(iter, t) != 0;
    }
    [LibraryImport(Gtk)] public static partial nint gtk_snapshot_new();
    [LibraryImport(Gtk)] public static partial void gtk_widget_snapshot_child(nint parent, nint child, nint snapshot);
    [LibraryImport(Gtk)] public static partial nint gtk_snapshot_free_to_node(nint snapshot);
    [LibraryImport(Gtk)] public static partial nint gtk_native_get_renderer(nint native);
    [LibraryImport(Gtk)] public static partial nint gsk_renderer_render_texture(nint renderer, nint node, nint viewport);
    [LibraryImport(Gtk)] public static partial void gsk_render_node_unref(nint node);
    [LibraryImport(Gtk)] public static partial int gdk_texture_get_width(nint texture);
    [LibraryImport(Gtk)] public static partial int gdk_texture_get_height(nint texture);
    [LibraryImport(Gtk)] public static partial void gdk_texture_download(nint texture, byte* data, nuint stride);
    [LibraryImport(GObject)] public static partial void g_object_unref(nint obj);

    // The window's content drawn as it is now (without the window's own
    // background), as BGRA bytes and its width; empty if it can't be drawn yet.
    public static (byte[] Pixels, int Width) Render(nint window)
    {
        var child = gtk_widget_get_first_child(window);
        var snapshot = gtk_snapshot_new();
        gtk_widget_snapshot_child(window, child, snapshot);
        var node = gtk_snapshot_free_to_node(snapshot);
        var renderer = gtk_native_get_renderer(window);
        if (node == 0 || renderer == 0) return ([], 0);
        var texture = gsk_renderer_render_texture(renderer, node, 0);
        gsk_render_node_unref(node);
        int w = gdk_texture_get_width(texture), h = gdk_texture_get_height(texture);
        var pixels = new byte[w * h * 4];
        fixed (byte* p = pixels) gdk_texture_download(texture, p, (nuint)(w * 4));
        g_object_unref(texture);
        return (pixels, w);
    }

    [LibraryImport(Adw)] public static partial nint adw_bottom_sheet_get_sheet(nint sheet);
    [LibraryImport(Adw)] public static partial nint adw_bottom_sheet_get_bottom_bar(nint sheet);
    [LibraryImport(Gtk)] public static partial int gtk_widget_get_width(nint widget);
    [LibraryImport(Gtk)] public static partial int gtk_widget_get_height(nint widget);

    // Whether widget is ancestor or under it.
    public static bool Inside(nint widget, nint ancestor)
    {
        for (var w = widget; w != 0; w = gtk_widget_get_parent(w)) if (w == ancestor) return true;
        return false;
    }

    // The nearest ancestor of this GType name, or 0.
    public static nint Ancestor(nint widget, string type)
    {
        for (var w = gtk_widget_get_parent(widget); w != 0; w = gtk_widget_get_parent(w)) if (TypeName(w) == type) return w;
        return 0;
    }

    // A widget's box relative to another (its top left, from gtk_widget_compute_point).
    public static (double X, double Y) BoxIn(nint widget, nint other)
    {
        float* p = stackalloc float[2], at = stackalloc float[2];
        p[0] = p[1] = 0;
        return gtk_widget_compute_point(widget, other, p, at) != 0 ? (at[0], at[1]) : (double.NaN, double.NaN);
    }

    [LibraryImport(Gtk)] public static partial nint gtk_widget_observe_controllers(nint widget);
    [LibraryImport(Gio)] public static partial nint g_list_model_get_item(nint model, uint position);
    [LibraryImport(GObject)] public static partial nint g_value_init(byte* value, nuint type);
    [LibraryImport(GObject)] public static partial void g_value_set_object(byte* value, nint obj);
    [LibraryImport(GObject)] public static partial void g_value_set_int(byte* value, int v);
    [LibraryImport(GObject)] public static partial void g_value_set_uint(byte* value, uint v);
    [LibraryImport(GObject)] public static partial void g_value_set_flags(byte* value, uint v);
    [LibraryImport(GObject)] public static partial void g_value_set_double(byte* value, double v);
    [LibraryImport(GObject)] public static partial int g_value_get_boolean(byte* value);
    [LibraryImport(GObject)] public static partial void g_value_unset(byte* value);
    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)] public static partial uint g_signal_lookup(string name, nuint itype);
    [LibraryImport(GObject)] public static partial void g_signal_emitv(byte* instanceAndParams, uint signalId, uint detail, byte* returnValue);
    [LibraryImport(Gtk)] public static partial nuint gdk_modifier_type_get_type();
    [LibraryImport(Gtk)] public static partial int gtk_label_get_ellipsize(nint label);
    [LibraryImport(Gtk)] public static partial nint gtk_label_get_layout(nint label);
    [LibraryImport("libpango-1.0.so.0")] public static partial int pango_layout_is_ellipsized(nint layout);
    [LibraryImport(Gtk)] public static partial uint gtk_drop_down_get_selected(nint dropDown);

    // The controllers of this GType on a widget.
    static List<nint> Controllers(nint widget, string type)
    {
        var model = gtk_widget_observe_controllers(widget);
        var found = new List<nint>();
        for (uint i = 0; i < g_list_model_get_n_items(model); i++)
        {
            var controller = g_list_model_get_item(model, i);
            if (TypeName(controller) == type) found.Add(controller);
            g_object_unref(controller); // the widget still holds it
        }
        g_object_unref(model);
        return found;
    }

    static nuint TypeOf(nint instance) => *(nuint*)*(nint*)instance; // G_TYPE_FROM_INSTANCE

    // As a mouse click does at (x, y) in widget's coordinates: every click
    // gesture on the widget gets pressed, then released (1 press), through
    // g_signal_emitv (the arguments are doubles: no varargs). The number of
    // click gestures the widget has (0: nothing was clicked).
    public static int ClickGestures(nint widget, double x, double y)
    {
        const nuint Int = 6 << 2, Double = 15 << 2;
        var gestures = Controllers(widget, "GtkGestureClick");
        foreach (var g in gestures)
            foreach (var signal in new[] { "pressed", "released" })
            {
                byte* v = stackalloc byte[4 * 24];
                new Span<byte>(v, 4 * 24).Clear();
                g_value_init(v, TypeOf(g)); g_value_set_object(v, g);
                g_value_init(v + 24, Int); g_value_set_int(v + 24, 1);
                g_value_init(v + 48, Double); g_value_set_double(v + 48, x);
                g_value_init(v + 72, Double); g_value_set_double(v + 72, y);
                g_signal_emitv(v, g_signal_lookup(signal, TypeOf(g)), 0, null);
                for (int i = 0; i < 4; i++) g_value_unset(v + i * 24);
            }
        return gestures.Count;
    }

    // As a key press in widget does: its key controllers' key-pressed, in
    // order, until one handles it. Whether one did; null if it has none.
    public static bool? PressKey(nint widget, uint keyval, uint state = 0)
    {
        const nuint UInt = 7 << 2, Boolean = 5 << 2;
        var keys = Controllers(widget, "GtkEventControllerKey");
        if (keys.Count == 0) return null;
        foreach (var k in keys)
        {
            byte* v = stackalloc byte[4 * 24];
            byte* ret = stackalloc byte[24];
            new Span<byte>(v, 4 * 24).Clear();
            new Span<byte>(ret, 24).Clear();
            g_value_init(v, TypeOf(k)); g_value_set_object(v, k);
            g_value_init(v + 24, UInt); g_value_set_uint(v + 24, keyval);
            g_value_init(v + 48, UInt); g_value_set_uint(v + 48, 0);
            g_value_init(v + 72, gdk_modifier_type_get_type()); g_value_set_flags(v + 72, state);
            g_value_init(ret, Boolean);
            g_signal_emitv(v, g_signal_lookup("key-pressed", TypeOf(k)), 0, ret);
            var handled = g_value_get_boolean(ret) != 0;
            for (int i = 0; i < 4; i++) g_value_unset(v + i * 24);
            g_value_unset(ret);
            if (handled) return true;
        }
        return false;
    }

    [LibraryImport(Gtk)] public static partial nint gtk_native_get_surface(nint native);
    [LibraryImport(Gtk)] public static partial void gtk_native_get_surface_transform(nint native, double* x, double* y);
    [LibraryImport(Gdk)] public static partial int gdk_popup_get_position_x(nint popup);
    [LibraryImport(Gdk)] public static partial int gdk_popup_get_position_y(nint popup);
    [LibraryImport(Gtk)] public static partial nint gtk_popover_get_child(nint popover);
    [LibraryImport(Gtk)] public static partial void gtk_widget_measure(nint widget, int orientation, int forSize, int* min, int* nat, int* minBaseline, int* natBaseline);
    [LibraryImport(Gtk)] public static partial void gtk_widget_queue_resize(nint widget);
    [LibraryImport(Gtk)] public static partial void gtk_widget_allocate(nint widget, int width, int height, int baseline, nint transform);
    const string Gdk = Gtk;

    // Where a point of widget is in its window's surface (the window's
    // shadow and CSD included), or null if GTK can't say yet.
    public static (double X, double Y)? InSurface(nint widget, nint window, double x, double y)
    {
        float* p = stackalloc float[2], at = stackalloc float[2];
        (p[0], p[1]) = ((float)x, (float)y);
        if (gtk_widget_compute_point(widget, window, p, at) == 0) return null;
        double sx, sy;
        gtk_native_get_surface_transform(window, &sx, &sy);
        return (at[0] + sx, at[1] + sy);
    }


    // Where a popover's frame shows, in its parent window's surface: its
    // surface's place (GdkPopup) plus where the popover sits in that surface
    // (its shadow), its width and height. Null while it isn't up.
    public static (double X, double Y, double W, double H)? PopoverBox(nint popover)
    {
        var surface = gtk_native_get_surface(popover);
        if (surface == 0 || gtk_widget_get_visible(popover) == 0) return null;
        double sx, sy;
        gtk_native_get_surface_transform(popover, &sx, &sy);
        return (gdk_popup_get_position_x(surface) + sx, gdk_popup_get_position_y(surface) + sy, gtk_widget_get_width(popover), gtk_widget_get_height(popover));
    }


    // Lays the window out now, at its present size, without waiting for a frame.
    public static void LayOut(nint window) => gtk_widget_allocate(window, gtk_widget_get_width(window), gtk_widget_get_height(window), -1, 0);

    // A widget's box in window's coordinates, as laid out now.
    public static (double X, double Y, int W, int H) BoxWH(nint widget, nint window)
    {
        float* p = stackalloc float[2], at = stackalloc float[2];
        p[0] = p[1] = 0;
        if (gtk_widget_compute_point(widget, window, p, at) == 0) return (double.NaN, double.NaN, 0, 0);
        return (at[0], at[1], gtk_widget_get_width(widget), gtk_widget_get_height(widget));
    }


    public static string Tooltip(nint widget) => Marshal.PtrToStringUTF8(gtk_widget_get_tooltip_text(widget)) ?? "";
    public static string Text(nint label) => Marshal.PtrToStringUTF8(gtk_label_get_text(label)) ?? "";

    // A widget's foreground colour as #RRGGBB.
    public static string Color(nint widget)
    {
        float* c = stackalloc float[4];
        gtk_widget_get_color(widget, c);
        return $"#{(int)Math.Round(c[0] * 255):X2}{(int)Math.Round(c[1] * 255):X2}{(int)Math.Round(c[2] * 255):X2}";
    }

    public static string TypeName(nint instance) => Marshal.PtrToStringUTF8(g_type_name_from_instance(instance)) ?? "";

    public static IEnumerable<nint> Descendants(nint widget)
    {
        for (var c = gtk_widget_get_first_child(widget); c != 0; c = gtk_widget_get_next_sibling(c))
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    public static IEnumerable<nint> OfType(nint root, string type) => Descendants(root).Where(w => TypeName(w) == type);

    // The first button under root with this label, or 0. Since the terminal
    // redesign a button reads "[ Start ]"; either form matches.
    public static nint Button(nint root, string label) =>
        OfType(root, "GtkButton").FirstOrDefault(b => Marshal.PtrToStringUTF8(gtk_button_get_label(b)) is { } l && (l == label || l == $"[ {label} ]"));

    public static List<string> Items(nint dropDown)
    {
        var model = gtk_drop_down_get_model(dropDown);
        var items = new List<string>();
        for (uint i = 0; i < g_list_model_get_n_items(model); i++) items.Add(Marshal.PtrToStringUTF8(gtk_string_list_get_string(model, i)) ?? "");
        return items;
    }

    public static int Cursor(nint textView)
    {
        var buffer = N.gtk_text_view_get_buffer(textView);
        byte* iter = stackalloc byte[256]; // GtkTextIter is 80 bytes
        gtk_text_buffer_get_iter_at_mark(buffer, iter, gtk_text_buffer_get_insert(buffer));
        return gtk_text_iter_get_offset(iter);
    }

    // The first line of a text view that shows in pane, wherever the
    // scrolling happens: in the view's own scrolled window or in one around it.
    public static int TopLine(nint textView, nint pane)
    {
        float* origin = stackalloc float[2], at = stackalloc float[2];
        origin[0] = origin[1] = 0;
        if (gtk_widget_compute_point(textView, pane, origin, at) == 0) return -1;
        int bx, by, top;
        gtk_text_view_window_to_buffer_coords(textView, 1, 0, (int)Math.Max(0, -at[1]), &bx, &by); // GTK_TEXT_WINDOW_WIDGET
        byte* iter = stackalloc byte[256];
        gtk_text_view_get_line_at_y(textView, iter, by, &top);
        return gtk_text_iter_get_line(iter);
    }

    // Scrolls the nearest scrolled window around widget that has room by
    // this much; false if none has.
    public static bool ScrollAround(nint widget, double by)
    {
        for (var w = gtk_widget_get_parent(widget); w != 0; w = gtk_widget_get_parent(w))
        {
            if (TypeName(w) != "GtkScrolledWindow") continue;
            var adjustment = N.gtk_scrolled_window_get_vadjustment(w);
            if (gtk_adjustment_get_upper(adjustment) - gtk_adjustment_get_page_size(adjustment) < by) continue;
            N.gtk_adjustment_set_value(adjustment, N.gtk_adjustment_get_value(adjustment) + by);
            return true;
        }
        return false;
    }

    public static void PlaceCursor(nint textView, int offset)
    {
        var buffer = N.gtk_text_view_get_buffer(textView);
        byte* iter = stackalloc byte[256];
        gtk_text_buffer_get_iter_at_offset(buffer, iter, offset);
        gtk_text_buffer_place_cursor(buffer, iter);
    }

    // stage 1 (the model picker): where a byte of a label's text
    // starts (pango units), and a texture saved as PNG for a look by eye.
    [LibraryImport("libpango-1.0.so.0")] public static partial void pango_layout_index_to_pos(nint layout, int index, int* rect);
    [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] public static partial int gdk_texture_save_to_png(nint texture, string filename);

    [LibraryImport(Gtk)] public static partial nint gtk_menu_button_get_popover(nint button);

    public static double XOfByte(nint label, int index)
    {
        int* rect = stackalloc int[4];
        pango_layout_index_to_pos(gtk_label_get_layout(label), index, rect);
        return rect[0] / 1024.0;
    }

    // The widget drawn on its own at its natural size (or the size it has), saved as PNG; false if it can't be drawn.
    public static bool SavePng(nint widget, nint native, string file)
    {
        int w = gtk_widget_get_width(widget), h = gtk_widget_get_height(widget);
        if (w <= 0 || h <= 0)
        {
            int min, nat, mb, nb, minH, natH;
            gtk_widget_measure(widget, 0, -1, &min, &nat, &mb, &nb);
            gtk_widget_measure(widget, 1, nat, &minH, &natH, &mb, &nb);
            gtk_widget_allocate(widget, nat, natH, -1, 0);
        }
        var snapshot = gtk_snapshot_new();
        gtk_widget_snapshot_child(gtk_widget_get_parent(widget), widget, snapshot);
        var node = gtk_snapshot_free_to_node(snapshot);
        var renderer = gtk_native_get_renderer(native);
        if (node == 0 || renderer == 0) return false;
        var texture = gsk_renderer_render_texture(renderer, node, 0);
        gsk_render_node_unref(node);
        var ok = gdk_texture_save_to_png(texture, file) != 0;
        g_object_unref(texture);
        return ok;
    }

    // stage 4 (dictation): a key let go, and the app's windows.
    [LibraryImport(Gtk)] public static partial nint gtk_window_get_toplevels();
    [LibraryImport(Adw)] public static partial nint adw_preferences_row_get_title(nint row);
    public static string RowTitle(nint row) => Marshal.PtrToStringUTF8(adw_preferences_row_get_title(row)) ?? "";
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_editable_get_selection_bounds(nint editable, int* start, int* end);

    // As letting go of a key in widget does: its key controllers' key-released.
    public static int ReleaseKey(nint widget, uint keyval, uint state = 0)
    {
        const nuint UInt = 7 << 2;
        var keys = Controllers(widget, "GtkEventControllerKey");
        foreach (var k in keys)
        {
            byte* v = stackalloc byte[4 * 24];
            new Span<byte>(v, 4 * 24).Clear();
            g_value_init(v, TypeOf(k)); g_value_set_object(v, k);
            g_value_init(v + 24, UInt); g_value_set_uint(v + 24, keyval);
            g_value_init(v + 48, UInt); g_value_set_uint(v + 48, 0);
            g_value_init(v + 72, gdk_modifier_type_get_type()); g_value_set_flags(v + 72, state);
            g_signal_emitv(v, g_signal_lookup("key-released", TypeOf(k)), 0, null);
            for (int i = 0; i < 4; i++) g_value_unset(v + i * 24);
        }
        return keys.Count;
    }

    // A real click in the middle of a widget, through its click gestures.
    public static int ClickMiddle(nint widget) => ClickGestures(widget, gtk_widget_get_width(widget) / 2.0, gtk_widget_get_height(widget) / 2.0);
}
