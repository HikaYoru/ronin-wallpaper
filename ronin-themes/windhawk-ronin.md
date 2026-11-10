# Ronin styles for Windhawk

Turn on Windows **Dark mode** first (Settings → Personalization → Colors).

How to apply each one: Windhawk → click the mod → **Details** → **Settings** tab. Set **Theme** to empty (none).
If the page has an **"Advanced" / code editor** toggle, paste the JSON below.
Otherwise, add each `target` + `styles` pair by hand under **Control styles** (one style per line).
Then click **Save settings**.

---

## Windows 11 Taskbar Styler

Charcoal frosted bar, a thin gold line along the top, gold "app is open" dots, gold-tinted hover.

```json
{
  "theme": "",
  "controlStyles": [
    {
      "target": "Taskbar.TaskbarFrame > Grid#RootGrid > Taskbar.TaskbarBackground > Grid > Rectangle#BackgroundFill",
      "styles": ["Fill:=<AcrylicBrush TintColor=\"#0F1012\" TintOpacity=\"0.78\" FallbackColor=\"#E60F1012\" />"]
    },
    {
      "target": "Rectangle#BackgroundStroke",
      "styles": ["Fill=#C9A24A", "Opacity=0.55"]
    },
    {
      "target": "Taskbar.TaskListButtonPanel@CommonStates > Grid > Border#BackgroundElement, Taskbar.TaskListButtonPanel@CommonStates > Border#BackgroundElement",
      "styles": [
        "CornerRadius=4",
        "Background@ActiveNormal=#1AC9A24A",
        "Background@InactivePointerOver=#26C9A24A",
        "Background@ActivePointerOver=#33C9A24A",
        "Background@InactivePressed=#40C9A24A",
        "Background@ActivePressed=#40C9A24A"
      ]
    },
    {
      "target": "Rectangle#RunningIndicator",
      "styles": ["Fill=#C9A24A"]
    },
    {
      "target": "Taskbar.TaskListLabeledButtonPanel@RunningIndicatorStates > Rectangle#RunningIndicator",
      "styles": ["Fill@InactiveRunningIndicator=#8A8D91", "Fill@ActiveRunningIndicator=#F2C35B", "Width@ActiveRunningIndicator=18"]
    }
  ]
}
```

## Windows 11 Start Menu Styler

Charcoal frosted panel with a thin gold border and a dark search box with a gold edge.

```json
{
  "theme": "",
  "controlStyles": [
    {
      "target": "Border#AcrylicBorder",
      "styles": [
        "Background:=<AcrylicBrush TintColor=\"#0F1012\" TintOpacity=\"0.85\" FallbackColor=\"#F20F1012\" />",
        "BorderBrush=#99C9A24A",
        "BorderThickness=1",
        "CornerRadius=6"
      ]
    },
    {
      "target": "Border#AcrylicOverlay",
      "styles": ["Visibility=Collapsed"]
    },
    {
      "target": "Border#TaskbarSearchBackground",
      "styles": ["Background=#1C1D21", "BorderBrush=#66C9A24A", "BorderThickness=1", "CornerRadius=4"]
    }
  ]
}
```

### Tweaking
- More see-through: lower `TintOpacity` (e.g. `0.6`).
- Brighter gold line: raise `Opacity` on `BackgroundStroke` toward `1`.
- Crimson instead of gold: replace `C9A24A` with `B3263B`.
- Undo: delete the entries (or disable the mod) and save.
