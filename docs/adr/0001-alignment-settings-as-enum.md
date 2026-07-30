# ADR-0001: Align settings stored as enum instead of string

## Status

Accepted. Implemented alongside the new `PlayingDetailAlignment` setting.

## Follow-up: `EffectivePlayingDetailAlignment` (portrait override layer)

`PlayingDetailAlignment` was later augmented with two additions:

1. `AppViewModel.IsPortraitLayout` (bool), pushed by `PlayingDetailPage` whenever the
   window-aspect-ratio hysteresis flips between landscape and portrait.
2. `AppViewModel.UsePlayingDetailAlignmentInPortrait` (bool, persisted; default `false`
   to preserve existing user behaviour on upgrade).

A derived read-only property `AppViewModel.EffectivePlayingDetailAlignment` collapses the
three inputs:

```
IsPortraitLayout && !UsePlayingDetailAlignmentInPortrait ? TextAlignment.Left : PlayingDetailAlignment
```

All `PlayingDetailPage.xaml` `TextAlignment` / `HorizontalAlignment` bindings now point at
`EffectivePlayingDetailAlignment` (mode `OneWay`, including the previously `OneTime`
`PlayingDetailInfoPanel` binding whose one-shot nature was a latent bug). Each input setter
fires `PropertyChanged(nameof(EffectivePlayingDetailAlignment))` so the resolution stays in
one place and the `PlayingDetailPage` no longer carries any orientation / alignment branch.

The ComboBox in both `SettingsPage` and `SettingsDialog` is deliberately **not** disabled
when the toggle is off: in landscape the ComboBox value still applies, and the user may
want to pre-pick an alignment while the window happens to be portrait.

## Context

The codebase originally modeled a few user-selectable settings as `string`
with hand-written values such as `"Left"`, `"Center"`, `"Right"` and routed them
through ComboBox `Tag`-value bindings. The first instance was `LyricsAlignment`
in `Model/SaveSettings.cs` (originally `string LyricsAlignment = "Left";`).

This convention has several drawbacks:

1. **Silent typos**: a typo in either the producer (the XAML `Tag`) or the
   consumer (the `switch` in `AppViewModel.SendLyricsSettings`) silently falls
   back to the default branch. There is no compile-time protection against
   drift.
2. **Mapping cost**: every consumer that needs the typed value has to write a
   `switch` expression from string to the target enum (e.g., to
   `Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment` for the Win2D bus).
3. **No help from the type system**: new enum members are not visible to the
   `switch` until a developer remembers to extend it.
4. **JSON migration friction is minimal**: serialization to numeric or string
   is acceptable for either format, and the existing `JsonStringEnumConverter`
   produces human-readable values.

## Decision

For alignment-style settings we adopt **strong-typed enums**:

- `SaveSettings.LyricsAlignment` is changed from `string` to
  `Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment` (the type the
  Win2D bus expects), decorated with
  `[JsonConverter(typeof(JsonStringEnumConverter))]` so the on-disk value
  stays readable.
- The new `SaveSettings.PlayingDetailAlignment` uses
  `Microsoft.UI.Xaml.TextAlignment` (the type XAML `TextBlock.TextAlignment`
  expects) with the same JSON converter.
- `AppViewModel` exposes those properties with the matching enum types and
  stops deriving intermediate `string`-based switch expressions.
- ComboBox UI keeps the existing `Tag="Left"/"Center"/"Right"` pattern because
  `.ToString()` of each enum member matches those tokens, so the binding
  `SelectedValue + SelectedValuePath="Tag"` keeps working.

## Consequences

- New enum members are surfaced automatically across producers and consumers.
- Old `settings.json` files containing `"LyricsAlignment": "Center"` (string)
  deserialize into the default enum value `Left`; on the next save the file
  is rewritten with the new format. **This silent fallback is intentional**
  per the project owner — no migration shim is added for a string-stored
  legacy value.
- Each persistence layer must use the enum value directly:
  - `MusicDatabaseService.SaveCurrentSettings` writes the enum value.
  - `MusicDatabaseService.LoadSettings` assigns the enum value.
  - `AppViewModel.SendLyricsSettings` passes the enum to
    `LyricsSettingsBus.Settings` without an intermediate translation.

## Alternatives considered

- **Keep `string`, add a converter for XAML binding**: rejected — keeps the
  typo risk and does not improve the Win2D-side dispatch path.
- **Numeric serialization for both fields**: rejected — readable string
  serialization is friendlier when debugging `settings.json`, and the
  converter is one attribute per field.

## Notes

- The ComboBox item keys (`Left`/`Center`/`Right`) intentionally overlap
  between `LyricsAlignment` and `PlayingDetailAlignment`. This is acceptable
  because the semantic is the same in both contexts; introducing distinct
  keys would multiply i18n work without clarifying the meaning.

## ComboBox binding bridge

XAML `x:Bind` performs strict compile-time type checking. The
`Selector.SelectedValue` property is typed `object`, but binding it directly
to an enum source with `SelectedValuePath="Tag"` (whose tags are `string`s)
fails compile and even at runtime, since `Object.Equals(enumValue, stringTag)`
returns false and the ComboBox cannot display the correct selected item.

Resolution: a single `EnumToStringConverter` (`Converters/EnumToStringConverter.cs`)
is registered in `Style/ConverterDictionary.xaml` and applied via the
`Converter={StaticResource EnumToStringConverter}` parameter on every
`SelectedValue` `x:Bind` for an alignment enum:

```xml
SelectedValue="{x:Bind ViewModel.AppViewModel.LyricsAlignment,
                      Mode=TwoWay,
                      Converter={StaticResource EnumToStringConverter}}"
```

The converter is generic over `targetType`, so the same instance is reused
for `CanvasHorizontalAlignment` and `TextAlignment`. The on-the-wire JSON form
remains the enum-name string (`"Left"`/`"Center"`/`"Right"`) so existing
ComboBox `Tag` values are unchanged.
