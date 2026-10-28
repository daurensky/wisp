# Onest application typography

Onest is the selected UI font, superseding the earlier gg sans and Google Sans
suggestions. Font binaries and their OFL notice come from the official
`google/fonts/ofl/onest` repository. Regular (400), Medium (500), Semibold (600)
and Bold (700) faces are embedded locally in WPF; Windows fonts are unchanged.

`Typography.Initialize` discovers the embedded `Onest` family and sets the
shared `Font.App` resource for windows, forms, controls, menus and tooltips.
Segoe UI is a safe fallback if the bundled resources are missing. Fonts are
never downloaded at runtime or installed into the operating system.

Check Cyrillic coverage, weight matching, text clipping and Windows DPI scaling
from 100 to 200 percent after changing font resources. Preserve the bundled OFL
notice when redistributing the app.
