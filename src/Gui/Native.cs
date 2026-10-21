using System.Runtime.InteropServices;

namespace Hive.Gui;

// GTK 4, libadwaita, GLib and cairo, bound by hand: only the calls the window
// makes. They load the first time `hive app` calls them, so the CLI and the
// MCP server never map GTK.
static unsafe partial class N
{
    const string GObject = "libgobject-2.0.so.0", GLib = "libglib-2.0.so.0", Gio = "libgio-2.0.so.0";
    const string Gtk = "libgtk-4.so.1", Adw = "libadwaita-1.so.0", Cairo = "libcairo.so.2";
    const StringMarshalling U = StringMarshalling.Utf8;

    // GtkBuilder templates name some types (AdwEntryRow's GSignalGroup) and
    // resolve them through the global symbol table, so the libraries have to
    // be loaded RTLD_GLOBAL before .NET loads them privately.
    [LibraryImport("libc.so.6", StringMarshalling = U)] private static partial nint dlopen(string file, int flags);

    public static void LoadGlobally()
    {
        foreach (var lib in new[] { GLib, GObject, Gio, Cairo, Gtk, Adw })
            if (dlopen(lib, 0x102) == 0) throw new HiveException($"Can't load {lib}; is GTK 4 and libadwaita installed?"); // RTLD_NOW | RTLD_GLOBAL
    }

    [LibraryImport(Gtk)] private static partial uint gtk_get_minor_version();
    [LibraryImport(Adw)] private static partial uint adw_get_minor_version();

    // The window calls gtk_css_provider_load_from_string (GTK 4.12) and
    // AdwBottomSheet (libadwaita 1.6); older ones would fail halfway through
    // building it, so say so before it starts.
    public static void CheckVersions()
    {
        uint gtk = gtk_get_minor_version(), adw = adw_get_minor_version();
        if (gtk < 12 || adw < 6)
            throw new HiveException($"The window needs GTK 4.12 and libadwaita 1.6 or newer; this system has GTK 4.{gtk} and libadwaita 1.{adw}.");
    }

    // GLib
    [LibraryImport(GObject, StringMarshalling = U)] public static partial ulong g_signal_connect_data(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    [LibraryImport(GObject)] public static partial void g_object_unref(nint obj);
    [LibraryImport(GObject)] public static partial nint g_object_ref(nint obj);
    // Varargs (name, value, ..., NULL); only ever called with one int property.
    [LibraryImport(GObject, StringMarshalling = U)] public static partial nint g_object_new(nint type, string name, int value, nint end);
    [LibraryImport(GObject)] public static partial nint g_type_class_ref(nint type);
    [LibraryImport(GObject, StringMarshalling = U)] public static partial nint g_enum_get_value_by_nick(nint enumClass, string nick);
    [LibraryImport(Gtk)] public static partial nint gtk_label_get_type();
    [LibraryImport(Gtk)] public static partial nint gtk_accessible_role_get_type();
    [LibraryImport(GLib)] public static partial uint g_timeout_add(uint ms, nint func, nint data);
    [LibraryImport(GLib)] public static partial void g_free(nint p);
    [LibraryImport(Gio)] public static partial int g_application_run(nint app, int argc, nint argv);
    [LibraryImport(Gio, StringMarshalling = U)] public static partial void g_set_application_name(string name);
    [LibraryImport(Gio, StringMarshalling = U)] public static partial nint g_file_new_for_path(string path);
    [LibraryImport(Gio)] public static partial nint g_file_get_path(nint file);
    [LibraryImport(Gio)] public static partial nint g_file_monitor_directory(nint file, int flags, nint cancellable, nint error);
    [LibraryImport(Gio)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool g_file_monitor_cancel(nint monitor);

    // widgets
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_hexpand(nint w, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_vexpand(nint w, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_halign(nint w, int align);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_valign(nint w, int align);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_margin_start(nint w, int m);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_margin_end(nint w, int m);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_margin_top(nint w, int m);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_margin_bottom(nint w, int m);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_widget_add_css_class(nint w, string css);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_size_request(nint w, int width, int height);
    [LibraryImport(Gtk)] public static partial void gtk_widget_queue_draw(nint w);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_visible(nint w, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_widget_get_visible(nint w);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_opacity(nint w, double opacity);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_can_target(nint w, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_parent(nint w, nint parent);
    [LibraryImport(Gtk)] public static partial int gtk_widget_get_width(nint w);
    // Varargs (property, value, ..., -1); only ever called with one string property.
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_accessible_update_property(nint accessible, int property, string value, int end);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_widget_set_tooltip_text(nint w, string? text);
    [LibraryImport(Gtk)] public static partial void gtk_widget_add_controller(nint w, nint controller);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_ancestor(nint w, nint type);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_focus_child(nint w);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_widget_grab_focus(nint w);
    [LibraryImport(Gtk)] public static partial void gtk_window_present(nint w);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_window_is_active(nint w);
    [LibraryImport(Gtk)] public static partial void gtk_window_set_default_size(nint w, int width, int height);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_window_set_title(nint w, string title);
    [LibraryImport(Gtk)] public static partial nint gtk_box_new(int orientation, int spacing);
    [LibraryImport(Gtk)] public static partial void gtk_box_append(nint box, nint child);
    [LibraryImport(Gtk)] public static partial void gtk_box_remove(nint box, nint child);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial nint gtk_label_new(string? text);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_label_set_text(nint l, string text);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_xalign(nint l, float x);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_wrap(nint l, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_selectable(nint l, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial nint gtk_button_new_with_label(string label);
    [LibraryImport(Gtk)] public static partial nint gtk_button_new();
    [LibraryImport(Gtk)] public static partial void gtk_button_set_child(nint button, nint child);
    [LibraryImport(Gtk)] public static partial nint gtk_scrolled_window_new();
    [LibraryImport(Gtk)] public static partial void gtk_scrolled_window_set_child(nint sw, nint child);
    [LibraryImport(Gtk)] public static partial void gtk_scrolled_window_set_policy(nint sw, int h, int v);
    [LibraryImport(Gtk)] public static partial nint gtk_scrolled_window_get_type();
    [LibraryImport(Gtk)] public static partial nint gtk_scrolled_window_get_vadjustment(nint sw);
    [LibraryImport(Gtk)] public static partial double gtk_adjustment_get_value(nint adjustment);
    [LibraryImport(Gtk)] public static partial void gtk_adjustment_set_value(nint adjustment, double value);
    [LibraryImport(Gtk)] public static partial nint gtk_list_box_new();
    [LibraryImport(Gtk)] public static partial nint gtk_flow_box_new();
    [LibraryImport(Gtk)] public static partial void gtk_flow_box_set_selection_mode(nint fb, int mode);
    [LibraryImport(Gtk)] public static partial void gtk_flow_box_set_max_children_per_line(nint fb, uint n);
    [LibraryImport(Gtk)] public static partial void gtk_flow_box_set_column_spacing(nint fb, uint n);
    [LibraryImport(Gtk)] public static partial void gtk_flow_box_set_row_spacing(nint fb, uint n);
    [LibraryImport(Gtk)] public static partial void gtk_flow_box_append(nint fb, nint child);
    [LibraryImport(Gtk)] public static partial void gtk_flow_box_remove_all(nint fb);
    [LibraryImport(Gtk)] public static partial void gtk_list_box_append(nint lb, nint child);
    [LibraryImport(Gtk)] public static partial void gtk_list_box_remove_all(nint lb);
    [LibraryImport(Gtk)] public static partial void gtk_list_box_set_selection_mode(nint lb, int mode);
    [LibraryImport(Gtk)] public static partial void gtk_list_box_set_placeholder(nint lb, nint w);
    [LibraryImport(Gtk)] public static partial int gtk_list_box_row_get_index(nint row);
    [LibraryImport(Gtk)] public static partial void gtk_list_box_row_set_activatable(nint row, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial nint gtk_editable_get_text(nint e);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_editable_set_text(nint e, string text);
    [LibraryImport(Gtk)] public static partial int gtk_editable_get_position(nint e);
    [LibraryImport(Gtk)] public static partial void gtk_editable_set_position(nint e, int position);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_editable_insert_text(nint e, string text, int length, int* position);
    [LibraryImport(Gtk)] public static partial nint gtk_text_view_new();
    [LibraryImport(Gtk)] public static partial nint gtk_text_view_get_buffer(nint tv);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_editable(nint tv, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_cursor_visible(nint tv, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_wrap_mode(nint tv, int mode);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_monospace(nint tv, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_left_margin(nint tv, int m);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_right_margin(nint tv, int m);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_top_margin(nint tv, int m);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_bottom_margin(nint tv, int m);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_text_buffer_set_text(nint buf, string text, int len);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_get_start_iter(nint buf, byte* iter);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_get_end_iter(nint buf, byte* iter);
    [LibraryImport(Gtk)] public static partial nint gtk_text_buffer_get_text(nint buf, byte* start, byte* end, [MarshalAs(UnmanagedType.Bool)] bool hidden);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_get_iter_at_offset(nint buf, byte* iter, int offset);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_delete(nint buf, byte* start, byte* end);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_text_buffer_insert(nint buf, byte* iter, string text, int len);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_begin_irreversible_action(nint buf);
    // Varargs (property, value, NULL): one string property, or one int one.
    [LibraryImport(Gtk, EntryPoint = "gtk_text_buffer_create_tag", StringMarshalling = U)] public static partial nint gtk_text_buffer_create_tag_color(nint buf, string name, string property, string value, nint end);
    [LibraryImport(Gtk, EntryPoint = "gtk_text_buffer_create_tag", StringMarshalling = U)] public static partial nint gtk_text_buffer_create_tag_weight(nint buf, string name, string property, int value, nint end);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_remove_all_tags(nint buf, byte* start, byte* end);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_text_buffer_apply_tag_by_name(nint buf, string name, byte* start, byte* end);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_text_buffer_get_iter_at_line_offset(nint buf, byte* iter, int line, int offset);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_text_buffer_get_iter_at_line(nint buf, byte* iter, int line);
    [LibraryImport(Gtk)] public static partial int gtk_text_buffer_get_line_count(nint buf);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_get_line_yrange(nint tv, byte* iter, int* y, int* height);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_buffer_to_window_coords(nint tv, int win, int bx, int by, int* wx, int* wy);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_gutter(nint tv, int win, nint widget);
    [LibraryImport(Gtk)] public static partial void gtk_text_view_set_pixels_below_lines(nint tv, int px);
    [LibraryImport(Gtk)] public static partial void gtk_text_buffer_end_irreversible_action(nint buf);
    [LibraryImport(Gtk)] public static partial nint gtk_css_provider_new();
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_css_provider_load_from_string(nint provider, string css);
    [LibraryImport(Gtk)] public static partial void gtk_style_context_add_provider_for_display(nint display, nint provider, uint priority);
    [LibraryImport(Gtk)] public static partial nint gdk_display_get_default();
    [LibraryImport(Gtk)] public static partial nint gtk_drawing_area_new();
    [LibraryImport(Gtk)] public static partial void gtk_drawing_area_set_draw_func(nint area, nint func, nint data, nint destroy);
    [LibraryImport(Gtk)] public static partial void gtk_drawing_area_set_content_height(nint area, int height);
    [LibraryImport(Gtk)] public static partial void gtk_drawing_area_set_content_width(nint area, int width);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial nint gtk_check_button_new_with_label(string label);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_check_button_get_active(nint button);
    [LibraryImport(Gtk)] public static partial void gtk_check_button_set_active(nint button, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial nint gtk_overlay_new();
    [LibraryImport(Gtk)] public static partial void gtk_overlay_set_child(nint overlay, nint child);
    [LibraryImport(Gtk)] public static partial void gtk_overlay_add_overlay(nint overlay, nint w);
    [LibraryImport(Gtk)] public static partial nint gtk_fixed_new();
    [LibraryImport(Gtk)] public static partial void gtk_fixed_put(nint f, nint w, double x, double y);
    [LibraryImport(Gtk)] public static partial void gtk_fixed_move(nint f, nint w, double x, double y);
    [LibraryImport(Gtk)] public static partial nint gtk_popover_new();
    [LibraryImport(Gtk)] public static partial void gtk_popover_set_child(nint p, nint child);
    [LibraryImport(Gtk)] public static partial void gtk_popover_set_pointing_to(nint p, int* rect);
    [LibraryImport(Gtk)] public static partial void gtk_popover_popup(nint p);
    [LibraryImport(Gtk)] public static partial void gtk_popover_popdown(nint p);
    [LibraryImport(Gtk)] public static partial void gtk_popover_set_position(nint p, int pos);
    [LibraryImport(Gtk)] public static partial nint gtk_menu_button_new();
    [LibraryImport(Gtk)] public static partial void gtk_menu_button_set_popover(nint b, nint popover);
    [LibraryImport(Gtk)] public static partial void gtk_menu_button_set_child(nint b, nint child);
    [LibraryImport(Gtk)] public static partial nint gtk_entry_new();
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_entry_grab_focus_without_selecting(nint e);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_entry_set_placeholder_text(nint e, string text);
    [LibraryImport(Gtk)] public static partial nint gtk_gesture_click_new();
    [LibraryImport(Gtk)] public static partial nint gtk_event_controller_motion_new();
    [LibraryImport(Gtk)] public static partial nint gtk_event_controller_key_new();
    [LibraryImport(Gtk)] public static partial void gtk_event_controller_set_propagation_phase(nint controller, int phase);
    [LibraryImport(Gtk)] public static partial double gtk_adjustment_get_upper(nint adjustment);
    [LibraryImport(Gtk)] public static partial double gtk_adjustment_get_page_size(nint adjustment);
    [LibraryImport(Gtk)] public static partial uint gtk_drop_down_get_selected(nint dd);
    [LibraryImport(Gtk)] public static partial nint gtk_string_list_new(byte** strings);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_string_list_append(nint list, string s);
    [LibraryImport(Gtk)] public static partial void gtk_string_list_remove(nint list, uint position);
    [LibraryImport(Gtk)] public static partial nint gtk_drop_down_new(nint model, nint expression);
    [LibraryImport(Gtk)] public static partial void gtk_drop_down_set_selected(nint dd, uint position);

    // libadwaita
    [LibraryImport(Adw, StringMarshalling = U)] public static partial nint adw_application_new(string id, int flags);
    [LibraryImport(Adw)] public static partial nint adw_application_window_new(nint app);
    [LibraryImport(Adw)] public static partial void adw_application_window_set_content(nint win, nint content);
    [LibraryImport(Adw)] public static partial nint adw_style_manager_get_default();
    [LibraryImport(Adw)] public static partial void adw_style_manager_set_color_scheme(nint manager, int scheme);
    [LibraryImport(Adw)] public static partial nint adw_toolbar_view_new();
    [LibraryImport(Adw)] public static partial void adw_toolbar_view_add_top_bar(nint tv, nint bar);
    [LibraryImport(Adw)] public static partial void adw_toolbar_view_add_bottom_bar(nint tv, nint bar);
    [LibraryImport(Adw)] public static partial void adw_toolbar_view_set_content(nint tv, nint content);
    [LibraryImport(Adw)] public static partial nint adw_header_bar_new();
    [LibraryImport(Adw)] public static partial void adw_header_bar_set_title_widget(nint hb, nint w);
    [LibraryImport(Adw)] public static partial void adw_header_bar_pack_start(nint hb, nint w);
    [LibraryImport(Adw)] public static partial void adw_header_bar_pack_end(nint hb, nint w);
    [LibraryImport(Adw)] public static partial void adw_header_bar_set_show_end_title_buttons(nint hb, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw)] public static partial void adw_header_bar_set_show_start_title_buttons(nint hb, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_window_close(nint w);
    [LibraryImport(Adw)] public static partial nint adw_view_stack_new();
    [LibraryImport(Adw, StringMarshalling = U)] public static partial nint adw_view_stack_add_titled_with_icon(nint stack, nint child, string name, string title, string icon);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_view_stack_set_visible_child_name(nint stack, string name);
    [LibraryImport(Adw)] public static partial nint adw_view_stack_get_visible_child_name(nint stack);
    [LibraryImport(Adw)] public static partial void adw_view_stack_page_set_badge_number(nint page, uint n);
    [LibraryImport(Adw)] public static partial void adw_view_stack_page_set_needs_attention(nint page, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial nint adw_breakpoint_condition_parse(string text);
    [LibraryImport(Adw)] public static partial nint adw_breakpoint_new(nint condition);
    [LibraryImport(Adw)] public static partial void adw_application_window_add_breakpoint(nint win, nint breakpoint);
    [LibraryImport(Adw)] public static partial nint adw_navigation_split_view_new();
    [LibraryImport(Adw)] public static partial void adw_navigation_split_view_set_sidebar(nint split, nint page);
    [LibraryImport(Adw)] public static partial void adw_navigation_split_view_set_content(nint split, nint page);
    [LibraryImport(Adw)] public static partial void adw_navigation_split_view_set_min_sidebar_width(nint split, double width);
    [LibraryImport(Adw)] public static partial void adw_navigation_split_view_set_max_sidebar_width(nint split, double width);
    [LibraryImport(Adw)] public static partial void adw_navigation_split_view_set_sidebar_width_fraction(nint split, double fraction);
    [LibraryImport(Adw)] public static partial void adw_navigation_split_view_set_collapsed(nint split, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw)] public static partial void adw_navigation_split_view_set_show_content(nint split, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool adw_navigation_split_view_get_show_content(nint split);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial nint adw_navigation_page_new(nint child, string title);
    // The Hive page's work pane: a column at the end, a fraction of the page wide.
    [LibraryImport(Adw)] public static partial nint adw_overlay_split_view_new();
    [LibraryImport(Adw)] public static partial void adw_overlay_split_view_set_sidebar(nint split, nint w);
    [LibraryImport(Adw)] public static partial void adw_overlay_split_view_set_content(nint split, nint w);
    [LibraryImport(Adw)] public static partial void adw_overlay_split_view_set_sidebar_position(nint split, int pack);
    [LibraryImport(Adw)] public static partial void adw_overlay_split_view_set_sidebar_width_fraction(nint split, double fraction);
    [LibraryImport(Adw)] public static partial void adw_overlay_split_view_set_sidebar_width_unit(nint split, int unit);
    [LibraryImport(Adw)] public static partial void adw_overlay_split_view_set_min_sidebar_width(nint split, double width);
    [LibraryImport(Adw)] public static partial void adw_overlay_split_view_set_max_sidebar_width(nint split, double width);
    [LibraryImport(Adw)] public static partial void adw_overlay_split_view_set_show_sidebar(nint split, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw)] public static partial nint adw_bottom_sheet_new();
    [LibraryImport(Adw)] public static partial void adw_bottom_sheet_set_content(nint sheet, nint w);
    [LibraryImport(Adw)] public static partial void adw_bottom_sheet_set_sheet(nint sheet, nint w);
    [LibraryImport(Adw)] public static partial void adw_bottom_sheet_set_bottom_bar(nint sheet, nint w);
    [LibraryImport(Adw)] public static partial nint adw_bin_new();
    [LibraryImport(Adw)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool adw_get_enable_animations(nint widget);
    [LibraryImport(Adw)] public static partial void adw_bin_set_child(nint bin, nint child);
    [LibraryImport(Adw)] public static partial nint adw_action_row_new();
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_action_row_set_subtitle(nint row, string text);
    [LibraryImport(Adw)] public static partial void adw_action_row_set_subtitle_lines(nint row, int lines);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_preferences_row_set_title(nint row, string title);
    [LibraryImport(Adw)] public static partial void adw_preferences_row_set_use_markup(nint row, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw)] public static partial nint adw_entry_row_new();
    [LibraryImport(Adw)] public static partial void adw_entry_row_set_show_apply_button(nint row, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw)] public static partial nint adw_switch_row_new();
    [LibraryImport(Adw)] public static partial void adw_switch_row_set_active(nint row, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool adw_switch_row_get_active(nint row);
    [LibraryImport(Adw)] public static partial nint adw_preferences_group_new();
    [LibraryImport(Adw)] public static partial void adw_preferences_group_add(nint group, nint w);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_preferences_group_set_title(nint group, string title);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_preferences_group_set_description(nint group, string? text);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial nint adw_alert_dialog_new(string heading, string? body);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_alert_dialog_add_response(nint d, string id, string label);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_alert_dialog_set_response_appearance(nint d, string id, int appearance);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_alert_dialog_set_response_enabled(nint d, string id, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_alert_dialog_set_default_response(nint d, string id);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_alert_dialog_set_close_response(nint d, string id);
    [LibraryImport(Adw)] public static partial void adw_alert_dialog_set_extra_child(nint d, nint child);
    [LibraryImport(Adw)] public static partial void adw_dialog_present(nint d, nint parent);
    [LibraryImport(Adw)] public static partial nint adw_dialog_new();
    [LibraryImport(Adw)] public static partial void adw_dialog_set_child(nint d, nint child);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_dialog_set_title(nint d, string title);
    [LibraryImport(Adw)] public static partial void adw_dialog_set_content_width(nint d, int w);
    [LibraryImport(Adw)] public static partial void adw_dialog_set_content_height(nint d, int h);
    [LibraryImport(Adw)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool adw_dialog_close(nint d);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_clipboard(nint w);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gdk_clipboard_set_text(nint clipboard, string text);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_yalign(nint l, float y);
    [LibraryImport(Adw)] public static partial nint adw_clamp_new();
    [LibraryImport(Adw)] public static partial void adw_clamp_set_child(nint clamp, nint child);
    [LibraryImport(Adw)] public static partial void adw_clamp_set_maximum_size(nint clamp, int size);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial nint adw_banner_new(string title);
    [LibraryImport(Adw, StringMarshalling = U)] public static partial void adw_banner_set_button_label(nint banner, string label);
    [LibraryImport(Adw)] public static partial void adw_banner_set_revealed(nint banner, [MarshalAs(UnmanagedType.Bool)] bool v);

    // the terminal skin: frames, markup, the frame clock, Pango, list item factories
    const string Pango = "libpango-1.0.so.0", PangoCairo = "libpangocairo-1.0.so.0";
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial nint gtk_frame_new(string? label);
    [LibraryImport(Gtk)] public static partial void gtk_frame_set_child(nint frame, nint child);
    [LibraryImport(Gtk)] public static partial void gtk_frame_set_label_widget(nint frame, nint label);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_label_set_markup(nint l, string markup);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_ellipsize(nint l, int mode);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_justify(nint l, int justify);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_max_width_chars(nint l, int n);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_lines(nint l, int lines);
    [LibraryImport(Gtk)] public static partial void gtk_label_set_wrap_mode(nint l, int mode);
    [LibraryImport(Gtk)] public static partial void gtk_scrolled_window_set_propagate_natural_height(nint sw, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial void gtk_scrolled_window_set_max_content_height(nint sw, int height);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial void gtk_widget_remove_css_class(nint w, string css);
    [LibraryImport(Gtk)] public static partial void gtk_widget_set_overflow(nint w, int overflow);
    [LibraryImport(Gtk)] public static partial void gtk_popover_set_has_arrow(nint p, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] public static partial nint gtk_overlay_get_child(nint overlay);
    [LibraryImport(Gtk)] public static partial nint gtk_frame_get_label_widget(nint frame);
    [LibraryImport(Gtk)] public static partial nint gtk_frame_get_child(nint frame);
    [LibraryImport(Gtk, StringMarshalling = U)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_widget_has_css_class(nint w, string css);
    [LibraryImport(Gtk)] public static partial nint gtk_revealer_new();
    [LibraryImport(Gtk)] public static partial void gtk_revealer_set_child(nint r, nint child);
    [LibraryImport(Gtk)] public static partial void gtk_revealer_set_reveal_child(nint r, [MarshalAs(UnmanagedType.Bool)] bool v);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_revealer_get_reveal_child(nint r);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_prev_sibling(nint w);
    [LibraryImport(Gtk)] public static partial void gtk_revealer_set_transition_type(nint r, int type);
    [LibraryImport(Gtk)] public static partial void gtk_revealer_set_transition_duration(nint r, uint ms);
    [LibraryImport(Gtk)] public static partial void gtk_box_insert_child_after(nint box, nint child, nint sibling);
    [LibraryImport(Gtk)] public static partial void gtk_box_prepend(nint box, nint child);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_first_child(nint w);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_next_sibling(nint w);
    [LibraryImport(Gtk)] public static partial nint gtk_widget_get_parent(nint w);
    [LibraryImport(Gtk)] public static partial void gtk_box_reorder_child_after(nint box, nint child, nint sibling);
    [LibraryImport(Gtk)] public static partial int gtk_widget_get_height(nint w);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_widget_get_mapped(nint w);
    [LibraryImport(Gtk)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool gtk_widget_compute_point(nint w, nint target, float* point, float* result);
    [LibraryImport(Gtk, StringMarshalling = U)] public static partial nint gtk_widget_create_pango_layout(nint w, string text);
    [LibraryImport(Pango, StringMarshalling = U)] public static partial void pango_layout_set_markup(nint layout, string markup, int length);
    [LibraryImport(Pango, StringMarshalling = U)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool pango_parse_markup(string markup, int length, uint accelMarker, nint attrs, nint text, nint accelChar, nint error);
    [LibraryImport(Pango)] public static partial void pango_layout_get_size(nint layout, int* width, int* height);
    [LibraryImport(Gtk)] public static partial int gtk_widget_get_margin_start(nint w);
    [LibraryImport(Gtk)] public static partial int gtk_widget_get_margin_end(nint w);
    [LibraryImport(Gtk)] public static partial uint gtk_widget_add_tick_callback(nint w, nint callback, nint data, nint destroy);
    [LibraryImport(Gtk)] public static partial long gdk_frame_clock_get_frame_time(nint clock);
    [LibraryImport(GLib)] public static partial long g_get_monotonic_time();
    [LibraryImport(Pango)] public static partial void pango_font_map_list_families(nint map, nint* families, int* n);
    [LibraryImport(Pango)] public static partial nint pango_font_family_get_name(nint family);
    [LibraryImport(PangoCairo)] public static partial nint pango_cairo_font_map_get_default();
    [LibraryImport(Gtk)] public static partial nint gtk_signal_list_item_factory_new();
    [LibraryImport(Gtk)] public static partial void gtk_list_item_set_child(nint item, nint child);
    [LibraryImport(Gtk)] public static partial nint gtk_list_item_get_child(nint item);
    [LibraryImport(Gtk)] public static partial nint gtk_list_item_get_item(nint item);
    [LibraryImport(Gtk)] public static partial nint gtk_string_object_get_string(nint obj);
    [LibraryImport(Gtk)] public static partial void gtk_drop_down_set_factory(nint dd, nint factory);
    [LibraryImport(Gtk)] public static partial void gtk_drop_down_set_list_factory(nint dd, nint factory);
    [LibraryImport(Adw)] public static partial void adw_entry_row_add_prefix(nint row, nint w);
    [LibraryImport(Gtk)] public static partial nint gtk_settings_get_default();
    [LibraryImport(GObject)] public static partial nint g_value_init(byte* value, nint type);
    [LibraryImport(GObject)] public static partial void g_value_set_double(byte* value, double v);
    [LibraryImport(GObject, StringMarshalling = U)] public static partial void g_object_set_property(nint obj, string name, byte* value);

    // cairo
    [LibraryImport(Cairo)] public static partial void cairo_set_source_rgba(nint cr, double r, double g, double b, double a);
    [LibraryImport(Cairo)] public static partial void cairo_move_to(nint cr, double x, double y);
    [LibraryImport(Cairo)] public static partial void cairo_line_to(nint cr, double x, double y);
    [LibraryImport(Cairo)] public static partial void cairo_fill(nint cr);
    [LibraryImport(Cairo)] public static partial void cairo_rectangle(nint cr, double x, double y, double w, double h);
    [LibraryImport(Cairo)] public static partial void cairo_stroke(nint cr);
    [LibraryImport(Cairo)] public static partial void cairo_set_line_width(nint cr, double w);
    [LibraryImport(Cairo)] public static partial void cairo_save(nint cr);
    [LibraryImport(Cairo)] public static partial void cairo_restore(nint cr);
    [LibraryImport(Cairo)] public static partial void cairo_scale(nint cr, double x, double y);
    [LibraryImport(Cairo)] public static partial void cairo_translate(nint cr, double x, double y);
    [LibraryImport(Cairo)] public static partial void cairo_close_path(nint cr);
    [LibraryImport(Cairo)] public static partial void cairo_new_path(nint cr);
    [LibraryImport(Cairo)] public static partial void cairo_fill_preserve(nint cr);
    [LibraryImport(Cairo)] public static partial void cairo_set_dash(nint cr, double* dashes, int n, double offset);
    [LibraryImport(PangoCairo)] public static partial nint pango_cairo_create_layout(nint cr);
    [LibraryImport(PangoCairo)] public static partial void pango_cairo_update_layout(nint cr, nint layout);
    [LibraryImport(PangoCairo)] public static partial void pango_cairo_show_layout(nint cr, nint layout);
    [LibraryImport(Pango)] public static partial int pango_layout_get_baseline(nint layout);
}
