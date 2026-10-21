namespace Hive.Gui;

// The window's own colours and its one terminal stylesheet: black, grays and
// smoky honey, always dark, in Fira Code (plus Symbols Nerd Font Mono for the
// icons), as the approved mockups have them. One source for the CSS and for
// what the scene and the Timeline draw, so they never drift apart. The CSS
// goes in at priority 801, above the user's gtk.css (800, which theming tools
// like matugen write from the wallpaper), and paints every container itself, so no theme
// colour shows through.
static class Palette
{
    // Grays, darkest first: the page, a panel, its border, the rules between
    // rows, a row under the pointer, the row picked.
    public const uint Black = 0x000000, Well = 0x050505, Panel = 0x0A0A09, Cell = 0x0E0D0C, Hover = 0x121210, Picked = 0x15130F;
    public const uint Sep = 0x1A1917, Rule = 0x1D1C1A, Line = 0x2B2A27, Border = 0x3A3936;
    // Text, faintest first.
    public const uint Ghost = 0x4A4844, Faint = 0x5F5D59, Dim = 0x8E8C88, Text = 0xC4C3C3, Bright = 0xE6E4E0;
    // Honey: the flight paths and lit frames, a done bar, the accent, its bright edge.
    public const uint Flight = 0x6B5534, Amber = 0x8A6D40, Done = 0x7A6239, Run = 0x9C7C4A, Honey = 0xB8935A, Pulse = 0xD4AE6E, Light = 0xE8C98E;
    public const uint QueenCell = 0x16130E, TalkCell = 0x1C1710, HoneyFill = 0x5C4628;
    // What's wrong and what's fine.
    public const uint Red = 0xE0846E, RedLine = 0x5A2E25, RedWell = 0x130C0A, Green = 0x8FB58A;

    public const string Font = "\"Fira Code\", \"Symbols Nerd Font Mono\", monospace";

    public static string Hex(uint c) => $"#{c:X6}";

    // Pango markup in one colour.
    public static string Span(uint c, string text) => $"<span foreground=\"#{c:X6}\">{Ui.Markup(text)}</span>";

    public static void Source(nint cr, uint c, double alpha = 1) =>
        N.cairo_set_source_rgba(cr, (c >> 16 & 255) / 255.0, (c >> 8 & 255) / 255.0, (c & 255) / 255.0, alpha);

    // Each colour as a GTK named colour (what the user's gtk.css paints
    // widgets with) and as a libadwaita variable (what libadwaita paints with).
    static readonly string[] names =
        ["window_bg", "window_fg", "view_bg", "view_fg", "headerbar_bg", "headerbar_fg", "headerbar_backdrop", "sidebar_bg", "sidebar_fg",
         "secondary_sidebar_bg", "card_bg", "card_fg", "popover_bg", "popover_fg", "dialog_bg", "dialog_fg", "accent", "accent_bg", "accent_fg",
         "border", "thumbnail_bg", "shade"];
    static readonly uint[] colors =
        [Black, Text, Black, Text, Black, Text, Black, Black, Text,
         Black, Panel, Text, Panel, Text, Panel, Text, Honey, Honey, Black,
         Line, Panel, Black];

    public static string Css()
    {
        var defines = new System.Text.StringBuilder();
        var vars = new System.Text.StringBuilder();
        for (int i = 0; i < names.Length; i++)
        {
            defines.Append($"@define-color {names[i]}_color {Hex(colors[i])};\n");
            vars.Append($"--{names[i].Replace('_', '-')}-color: {Hex(colors[i])}; ");
        }
        string black = Hex(Black), well = Hex(Well), panel = Hex(Panel), hover = Hex(Hover), picked = Hex(Picked), sep = Hex(Sep), rule = Hex(Rule),
            line = Hex(Line), border = Hex(Border), ghost = Hex(Ghost), faint = Hex(Faint), dim = Hex(Dim), text = Hex(Text), bright = Hex(Bright),
            flight = Hex(Flight), honey = Hex(Honey), pulse = Hex(Pulse), light = Hex(Light), fill = Hex(HoneyFill), red = Hex(Red), redLine = Hex(RedLine),
            redWell = Hex(RedWell), green = Hex(Green);
        return $$"""
            {{defines}}@define-color borders {{line}};
            @define-color error_color {{red}};
            @define-color destructive_color {{red}};
            :root { {{vars}}--dim-opacity: 100%; --window-radius: 0; --border-opacity: 100%; }
            * { font-family: {{Font}}; border-radius: 0; -gtk-icon-shadow: none; outline-color: {{flight}}; }
            window, .background, dialog, popover { background-color: {{black}}; color: {{text}}; font-size: 13px; }
            .monospace, textview, textview text { font-family: {{Font}}; }

            /* No theme colour shows through: every container libadwaita or the
               user's gtk.css might paint is see-through, so the window's black is
               all there is behind the panels. */
            toolbarview, toolbarview > .top-bar, toolbarview > .bottom-bar, navigation-view, navigation-split-view,
            navigation-split-view > .sidebar-pane, navigation-split-view > .content-pane, navigation-page, stack,
            scrolledwindow, viewport, bottom-sheet, bottom-sheet > sheet, clamp, overlay, fixed, revealer,
            windowhandle, widget.sidebar-pane, .sidebar-pane, .content-pane, .view, .navigation-sidebar, overlay-split-view
            { background: none; background-color: transparent; box-shadow: none; color: {{text}}; }
            navigation-split-view > .sidebar-pane, overlay-split-view > .sidebar-pane { border: none; }
            scrolledwindow > undershoot, scrolledwindow > overshoot, scrolledwindow > junction { background: none; box-shadow: none; }
            bottom-sheet > sheet { background-color: {{panel}}; border-top: 1px solid {{line}}; }
            bottom-sheet > dimming { background-color: rgba(0, 0, 0, 0.6); }
            selection, text selection { background-color: {{fill}}; color: {{bright}}; }

            /* text */
            .dim-label { color: {{dim}}; opacity: 1; }
            .faint { color: {{faint}}; }
            .ghost { color: {{ghost}}; }
            .bright { color: {{bright}}; }
            .honey { color: {{pulse}}; }
            .bad { color: {{red}}; }
            .good { color: {{green}}; }
            .small { font-size: 12px; }
            .tiny { font-size: 11px; }
            .title-1 { font-size: 22px; font-weight: 700; color: {{bright}}; }
            .title-2 { font-size: 19px; font-weight: 700; color: {{bright}}; }
            .title-3, .title-4 { font-size: 15px; font-weight: 700; color: {{bright}}; }
            .heading { font-weight: 700; color: {{bright}}; }
            .caps { font-size: 12px; color: {{dim}}; letter-spacing: 0.06em; }
            .caption, .caption-heading { font-size: 11px; }
            .prompt { color: {{pulse}}; }
            .entry-prompt { margin-right: 10px; }
            .art { color: {{dim}}; }
            .status-art { color: {{pulse}}; font-size: 18px; }

            /* the header: the brand, the tabs in the middle, the counts and ✕ */
            headerbar { background-color: {{black}}; box-shadow: none; border-bottom: 1px solid {{rule}}; min-height: 44px; padding: 0 20px; }
            headerbar > windowhandle > box { padding: 0; }
            .brand-sprite { color: {{pulse}}; font-size: 14px; }
            .brand { color: {{bright}}; font-weight: 700; letter-spacing: 0.04em; }
            .counts { color: {{dim}}; font-size: 12px; }
            button.close { color: {{dim}}; padding: 4px 6px; border: none; background: none; }
            button.close:hover { color: {{pulse}}; }
            menubutton.inbox-button > button { color: {{pulse}}; padding: 3px 8px; border: 1px solid transparent; }
            menubutton.inbox-button > button:hover, menubutton.inbox-button > button:checked { border-color: {{flight}}; color: {{light}}; }
            menubutton.inbox-button.empty > button { color: {{faint}}; }
            .inbox-badge { font-size: 12px; }
            .tabs .tab { color: {{dim}}; background: none; padding: 0 2px; margin: 0 11px; min-height: 42px; font-size: 13px;
                         border: none; border-bottom: 2px solid transparent; transition: color 150ms, border-color 150ms; }
            .tabs .tab:hover { color: {{text}}; }
            .tabs .tab.on { color: {{bright}}; border-bottom-color: {{honey}}; }
            .bottom-tabs { border-top: 1px solid {{rule}}; }
            .bottom-tabs .tab { min-height: 36px; }

            /* panels: a 1 px border on near-black, the title in the border on
               black, an optional label at the right of the border */
            frame.panel { border: 1px solid {{line}}; background-color: {{panel}}; padding: 0; }
            frame.panel.drawing { border-color: transparent; }
            frame.panel.lit { border-color: {{flight}}; }
            frame.panel.term { border-color: {{flight}}; background-color: {{well}}; }
            frame.panel > label.panel-title, .panel-title-box { color: {{text}}; background-color: {{black}}; margin-left: 11px; padding: 0 6px; font-size: 12px; }
            .panel-right { color: {{dim}}; background-color: {{black}}; margin-right: 12px; padding: 0 6px; font-size: 12px; }
            .panel-right button { padding: 0 6px; }
            .legend { font-size: 11px; color: {{faint}}; border-top: 1px solid {{sep}}; padding: 6px 4px 0 4px; }

            /* buttons: a 1 px box, honey under the pointer; the main action is honey */
            button { background: none; border: none; box-shadow: none; color: {{text}}; padding: 0; min-height: 0; min-width: 0;
                     transition: color 150ms, background-color 150ms, border-color 150ms; }
            button:hover { background: none; color: {{pulse}}; }
            button:active, button:checked { background: none; }
            button.btn { border: 1px solid {{border}}; padding: 3px 10px; font-size: 12px; color: {{text}}; }
            button.btn:hover { border-color: {{honey}}; color: {{pulse}}; }
            button.btn.suggested-action, button.btn.main { background-color: {{honey}}; border-color: {{honey}}; color: {{panel}}; font-weight: 500; }
            button.btn.suggested-action:hover, button.btn.main:hover { background-color: {{pulse}}; border-color: {{pulse}}; color: {{black}}; }
            button.btn.destructive-action { color: {{red}}; border-color: {{redLine}}; }
            button.btn.destructive-action:hover { color: {{black}}; background-color: {{red}}; border-color: {{red}}; }
            button.btn.flat { border-color: {{line}}; color: {{dim}}; }
            button.btn.flat:hover { border-color: {{honey}}; color: {{pulse}}; }
            button.btn.honeyed { border-color: {{flight}}; color: {{pulse}}; }
            button.btn.wide { padding: 5px 10px; }
            button.link { color: {{pulse}}; padding: 0; }
            button.link:hover { color: {{light}}; }
            button:disabled, button:disabled label { color: {{ghost}}; background: none; border-color: {{line}}; }
            button:focus-visible { outline: 1px dashed {{honey}}; outline-offset: -1px; }
            windowcontrols button, windowcontrols button image { color: {{dim}}; background: none; box-shadow: none; }

            /* entries and text: black, a 1 px border, a honey caret */
            entry, text, textview, textview text, searchbar { background-color: {{black}}; color: {{bright}}; caret-color: {{pulse}}; box-shadow: none; }
            entry { border: 1px solid {{line}}; outline: none; min-height: 0; padding: 5px 10px; font-size: 12px; }
            entry:focus-within { border-color: {{flight}}; outline: none; }
            entry image { color: {{dim}}; }
            entry placeholder, text placeholder, textview text placeholder { color: {{ghost}}; }
            .bare entry, entry.bare { border: none; padding: 0; background: none; }
            .field { border: 1px solid {{border}}; background-color: {{black}}; padding: 7px 10px; }
            .field:focus-within { border-color: {{flight}}; }
            textview.frame, .frame { border: 1px solid {{line}}; background-color: {{black}}; }
            textview:focus-within.frame, .frame:focus-within { border-color: {{flight}}; }

            /* drop-downs: a plain box, "choice ▾" */
            dropdown > button { border: 1px solid {{line}}; background-color: {{black}}; padding: 3px 8px; color: {{text}}; font-size: 12px; }
            dropdown > button:hover { border-color: {{honey}}; color: {{pulse}}; }
            dropdown arrow { min-width: 0; min-height: 0; -gtk-icon-source: none; margin: 0; }
            popover > contents, popover > arrow { background-color: {{panel}}; border: 1px solid {{line}}; box-shadow: none; color: {{text}}; }
            popover listview row, popover list row { padding: 3px 10px; font-size: 12px; }
            popover listview row:selected, popover listview row:hover { background-color: {{honey}}; color: {{black}}; }
            popover entry, popover dropdown > button, popover textview, popover textview text { background-color: {{black}}; }
            popover.menu > contents { background-color: {{black}}; padding: 2px; }

            /* lists: rows separated by a 1 px rule; hover and the row picked are a shade lighter */
            list, list.boxed-list, .boxed-list, listview { background: none; border: none; box-shadow: none; color: {{text}}; }
            list > row, .boxed-list > row { background: none; border: none; border-top: 1px solid {{sep}}; padding: 0; transition: background-color 150ms; }
            list > row:first-child, .boxed-list > row:first-child { border-top: none; }
            list > row:hover, .boxed-list > row.activatable:hover, .item:hover { background-color: {{hover}}; }
            list > row.on, .item.on { background-color: {{picked}}; }
            list > row:focus-visible { outline: none; box-shadow: inset 2px 0 {{honey}}; }
            list > row:selected { background-color: {{picked}}; color: {{text}}; }
            row .title { color: {{bright}}; }
            row .subtitle { color: {{dim}}; font-size: 12px; }
            preferencesgroup > box > label.title { color: {{dim}}; }
            .boxed-list > row.entry, row.entry { background-color: {{black}}; }
            .item { padding: 10px 16px; }
            .row-sep { border-top: 1px solid {{sep}}; }
            .chip { border: 1px solid {{border}}; color: {{text}}; padding: 0 6px; font-size: 11px; }
            .tag { border: 1px solid {{border}}; color: {{dim}}; padding: 0 6px; font-size: 11px; }
            .tag.bad { color: {{red}}; border-color: {{redLine}}; }
            .tag.honey { color: {{pulse}}; border-color: {{flight}}; }

            /* switches: a box that fills honey */
            switch { background-color: {{black}}; border: 1px solid {{border}}; min-width: 34px; min-height: 16px; }
            switch:checked { background-color: {{honey}}; border-color: {{honey}}; }
            switch > slider { background-color: {{dim}}; box-shadow: none; border: none; min-width: 14px; min-height: 14px; }
            switch:checked > slider { background-color: {{black}}; }
            checkbutton check { border: 1px solid {{border}}; background: {{black}}; min-width: 12px; min-height: 12px; }
            checkbutton check:checked { background-color: {{honey}}; border-color: {{honey}}; color: {{black}}; }

            scrollbar { background: none; border: none; }
            scrollbar slider { background-color: {{line}}; min-width: 4px; min-height: 4px; }
            scrollbar slider:hover { background-color: {{dim}}; }

            dialog floating-sheet, dialog sheet, .alert sheet { border: 1px solid {{line}}; box-shadow: none; background-color: {{panel}}; }
            banner > revealer > widget { background-color: {{panel}}; color: {{pulse}}; border-bottom: 1px solid {{line}}; }
            separator { background-color: {{rule}}; min-height: 1px; min-width: 1px; }

            /* the hive page */
            .scene { font-family: {{Font}}; }
            .work-row { padding: 8px 16px 10px 16px; }
            .work-spin { color: {{pulse}}; min-width: 8px; }
            .work-spin.stopped { color: {{dim}}; }
            .work-bee { color: {{bright}}; font-weight: 500; }
            .work-title { color: {{text}}; }
            .work-time { color: {{dim}}; font-size: 12px; }
            .work-line { color: {{dim}}; font-size: 12px; }
            .work-tree { color: {{ghost}}; }
            .work-cursor { color: {{pulse}}; font-size: 12px; }
            .work-bar { color: {{honey}}; font-size: 12px; letter-spacing: 0.05em; }
            .work-info { color: {{faint}}; font-size: 12px; }
            .inbox-item { padding: 10px 16px; transition: background-color 150ms; }
            .inbox-item:hover { background-color: {{hover}}; }
            .inbox-icon { color: {{dim}}; min-width: 16px; }
            .inbox-icon.done, .inbox-icon.review, .inbox-icon.ask { color: {{pulse}}; }
            .inbox-icon.failed { color: {{red}}; }
            .inbox-title { color: {{bright}}; }
            .inbox-sub { color: {{dim}}; font-size: 12px; }
            .inbox-count, .odometer { color: {{dim}}; font-size: 12px; }
            .timeline-name { color: {{text}}; font-size: 12px; }
            .group-head { color: {{dim}}; font-size: 12px; letter-spacing: 0.06em; }
            .group-line { color: {{ghost}}; }
            .group-rule { border-top: 1px solid {{sep}}; min-height: 0; }
            .task-view-mark { font-size: 20px; color: {{pulse}}; min-width: 28px; }
            .step-log { font-size: 12px; }
            .task-mark { color: {{dim}}; min-width: 16px; }
            .task-mark.done { color: {{green}}; }
            .task-mark.failed { color: {{red}}; }
            .fm-rule, .fm-key { color: {{faint}}; font-size: 12px; }
            .fm-id { color: {{pulse}}; }
            .fm-title { font-weight: 700; }
            .fm-link { color: {{pulse}}; padding: 0; }
            .fm-link:hover { color: {{light}}; }
            .meter-count { color: {{bright}}; font-size: 20px; }
            .meter-count.bad { color: {{red}}; }
            .meter-legend { color: {{dim}}; font-size: 12px; }

            /* reviews: the list, the pipeline, the rendered report */
            .run-row { padding: 12px 16px 12px 10px; }
            .run-marker { opacity: 0; }
            row.on .run-marker { opacity: 1; }
            .pipe-box { border: 1px solid {{border}}; padding: 4px 10px; }
            .pipe-box.overseer { border-color: {{flight}}; padding: 8px 12px; }
            .honey-row { border-top: 1px solid {{rule}}; padding-top: 16px; }
            .verdict { border: 1px solid {{redLine}}; background-color: {{redWell}}; padding: 14px 18px; margin-top: 4px; }
            .verdict.ok { border-color: #2B3A29; background-color: #0B100A; }
            .verdict-title { font-weight: 700; }
            .md-section { font-size: 13px; font-weight: 500; letter-spacing: 0.06em; margin-top: 20px; }
            .sub-section { margin-top: 8px; }
            .md-number { color: {{pulse}}; }
            .md-bullet { color: {{flight}}; }
            .quote { margin-left: 4px; }
            .finding { padding: 14px 0; border-bottom: 1px solid {{sep}}; }
            .code { background-color: {{black}}; border: 1px solid {{rule}}; }
            .code-text { padding: 10px 12px; font-size: 12px; color: {{text}}; }

            /* memory */
            flowbox, flowboxchild { background: none; padding: 0; border: none; }
            button.chip-filter { border: 1px solid {{line}}; color: {{dim}}; font-size: 11px; padding: 1px 7px; }
            button.chip-filter:hover { color: {{text}}; border-color: {{border}}; }
            button.chip-filter.on { color: {{panel}}; background-color: {{honey}}; border-color: {{honey}}; }
            .file-strip { background-color: {{well}}; border-bottom: 1px solid {{line}}; padding: 18px 24px 14px 24px; }
            .entry-title { font-size: 18px; font-weight: 700; }
            .summary-box { background-color: #0F0E0C; border: 1px solid {{rule}}; padding: 12px 16px; }
            .linked { border-top: 1px solid {{rule}}; padding-top: 14px; margin-top: 26px; }

            /* the work pane's Queen tab */
            .talk-tabs { padding: 0 20px; border-bottom: 1px solid {{rule}}; }
            button.talk-chip { color: {{dim}}; font-size: 12px; padding: 8px 2px; border-bottom: 2px solid transparent; }
            button.talk-chip:hover { color: {{text}}; }
            button.talk-chip.on { color: {{bright}}; border-bottom-color: {{honey}}; }
            button.talk-new { color: {{dim}}; font-size: 12px; padding: 8px 2px; }
            button.talk-new:hover { color: {{pulse}}; }
            menubutton.talk-model > button { padding: 0 4px; margin: -2px 0; min-height: 0; border: 1px solid transparent; }
            menubutton.talk-model > button:hover, menubutton.talk-model > button:checked { border-color: {{flight}}; }
            button.talk-model-choice { color: {{text}}; font-size: 12px; padding: 4px 10px; }
            button.talk-model-choice:hover { color: {{pulse}}; }
            button.talk-mic { color: {{dim}}; padding: 0 4px; min-height: 0; }
            button.talk-mic:hover { color: {{pulse}}; }
            button.talk-mic.on { color: {{red}}; }
            .talk-line.talk-you { margin-top: 12px; background-color: {{hover}}; padding: 4px 8px; margin-left: -8px; }
            .talk-line.talk-say, .talk-line.talk-tool, .talk-line.talk-queen { margin-top: 12px; }
            .talk-line.talk-out, .talk-line.talk-plain, .talk-line.talk-err { margin-left: 20px; }
            .talk-line.talk-del { margin-left: 40px; background-color: #1A0F0D; }
            .talk-line.talk-add { margin-left: 40px; background-color: #0F1A0E; }
            .talk-prompt { border-top: 1px solid {{border}}; border-bottom: 1px solid {{border}}; padding: 9px 2px; }
            entry.talk-input { font-size: 13px; color: {{bright}}; }
            .talk-status { padding: 8px 26px 14px 26px; }
            .talk-watch { margin: 0 24px; border-top: 1px solid {{line}}; padding: 10px 2px 12px 2px; }

            /* briefs */
            .code-editor, .code-editor text { background-color: {{well}}; font-size: 12px; color: {{text}}; }
            .editor-bar { border-top: 1px solid {{rule}}; padding: 10px 16px; }
            .meter-note { border: 1px solid {{rule}}; background-color: {{well}}; padding: 10px 12px; }

            /* a bee's page */
            .card-change { padding: 6px 16px 10px 16px; }
            .card-change.row-sep { padding-top: 10px; }
            .card-section { margin-top: 10px; }

            /* Claude usage */
            button.usage { padding: 0; }
            .usage-percent { font-size: 20px; color: {{pulse}}; }
            .usage-more { border-top: 1px solid {{rule}}; padding-top: 12px; }

            /* the status line at the foot of the window */
            .statusline { border-top: 1px solid {{rule}}; padding: 0 20px; min-height: 34px; background-color: {{black}}; font-size: 12px; }
            .statusline .status-msg { color: {{text}}; }
            .statusline .status-msg.error { color: {{red}}; }
            .statusline button.btn { padding: 1px 8px; }
            .countdown { color: {{pulse}}; }
            """;
    }
}
