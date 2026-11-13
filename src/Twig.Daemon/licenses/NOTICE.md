# Microsoft icon assets

Shared control icons use the 20px SVG artwork from Microsoft
Fluent UI System Icons, pinned to commit
`e15fc0de25fab983e3e42bac8798be69a85c23bf`:
https://github.com/microsoft/fluentui-system-icons

The unmodified SVG bytes embedded as data URLs in `wwwroot/icons.css` are Add, Arrow Clockwise,
Text Bullet List, Organization, Plug Connected, Plug Disconnected, Settings,
Group List, Document Add, Clipboard Task, Chevron Right and Chevron Down,
respectively mapped to the existing shared icon classes. Copyright (c) 2020
Microsoft Corporation. The MIT license is preserved verbatim as
`fluentui-system-icons.LICENSE`. Local CSS masks inherit the existing current
color and em sizing without a web-font dependency or new static-file routes.
The source SVGs are preserved in `tests/fixtures/fluent-icons/` for exact
byte and raster comparisons.

Work-item rows additionally embed Circle (regular and filled), Checkmark Circle,
Dismiss Circle, Question Circle, Person Circle, Copy,
Document Edit, Layer, Edit and Subtract Circle from that same pinned revision,
under the same MIT license. Their unmodified SVG source fixtures are also in
`tests/fixtures/fluent-icons/`.

The dotted-circle status SVG was authored for Twig's Unknown category, rather
than imported from Microsoft. Its source is in `tests/fixtures/state-icons/`.

The Azure DevOps and GitHub provider marks are unmodified Microsoft **Codicons**
artwork, retrieved on 2026-11-12 from:
https://github.com/microsoft/vscode-codicons/tree/main/src/icons
(`azure-devops.svg` and `github.svg`). Copyright Microsoft Corporation.
These two assets are licensed under **CC BY 4.0**:
https://creativecommons.org/licenses/by/4.0/
No changes were made to the SVG artwork; CSS scales and colors its masks.
Source fixtures are preserved in `tests/fixtures/codicons/`.

The retained legacy font `fluent-regular-v1.1.293.woff2` comes from
**azure-devops-ui 2.281.0**,
`Components/Icon/FluentIcons.css` and `Components/Icon/fonts/`.

Copyright Microsoft Corporation. The package MIT license is preserved verbatim
as `azure-devops-ui.LICENSE`. Source:
https://github.com/microsoft/azure-devops-ui-public

The old font remains an installation-package asset but is not used by shared
control rendering. No component framework or
Windows font files are copied. Segoe UI is referenced as an installed system
font. The palette and compact control treatment follow Azure DevOps' official
`Core/_platformCommon.scss` semantic communication/neutral vocabulary, with
documented Copilot semantic theme-variable fallbacks.
