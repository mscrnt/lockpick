# Third-Party Notices

## Included in Lockpick

### Lucide icons — ISC License
Icon path data in `src/Lockpick.App/Assets/Icons.axaml` comes from Lucide (https://lucide.dev),
lucide-static 0.460.0. `rect`/`circle` elements were converted to equivalent path data.

```
ISC License

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT).
All other copyright (c) for Lucide are held by Lucide Contributors 2022.

Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is
hereby granted, provided that the above copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE
INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE
FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING
OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

### NuGet packages (referenced, not vendored)

| Package | License |
|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | MIT |
| CommunityToolkit.Mvvm | MIT |
| Velopack | MIT |
| Microsoft.Extensions.Logging(.Abstractions) | MIT |
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| BenchmarkDotNet | MIT |

## Research references (no code included)

Memory addresses, structure offsets and file-format facts were learned from the projects below and
verified independently against the GOG executable. No source code from them is included in Lockpick.

| Project | License | Notes |
|---|---|---|
| ecruells/resident-evil-pc-decomp | GPL-3.0 | Global addresses and entity layout for the GOG USA build |
| Gemini-Loboto3/RE1-Mod-SDK | CC BY-NC-ND 4.0 | Struct layout cross-check |
| deserteagle417/RE1-Autosplitter | No license file | Address leads (credited) |
| biorand/classic, biorand/biohazard-utils | MIT | RDT format and id tables (future use with attribution) |
| shadwork/Windows-ResidentEvil1-MODS | MIT | Layout cross-check |

Resident Evil is a trademark of Capcom Co., Ltd. Lockpick is not affiliated with Capcom or GOG and does
not distribute any game files or assets.
