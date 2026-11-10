# Ronin themes

Same palette as the wallpaper:

| Role | Hex |
|---|---|
| Ink (background) | `#0F1012` |
| Stone (panels) | `#2A2C30` |
| Ash (secondary text) | `#8A8D91` |
| Bone (main text) | `#D9D6CF` |
| Ember (accent) | `#C9A24A` / glow `#F2C35B` |
| Blood (optional 2nd accent) | `#B3263B` |

## Windows accent color

Settings → Personalization → Colors:
- Choose your mode: **Dark**
- Transparency effects: **On**
- Accent color: **Manual** → View colors → More → type `C9A24A`
- Turn on "Show accent color on Start and taskbar" if you want gold there too.

## Windows Terminal

1. Open Windows Terminal → `Ctrl + ,` → bottom-left **Open JSON file**.
2. Find the `"schemes": [ ... ]` list. Paste the `{ "name": "Ronin", ... }` block from
   `windows-terminal-ronin.json` inside it (add a comma after the previous scheme if there is one).
3. Optional: copy the `"defaults"` values into `"profiles" > "defaults"` for the acrylic,
   bar cursor and padding.
4. Save. Or, instead of step 3, pick **Ronin** under each profile's Appearance → Color scheme.

## Spotify (Spicetify)

> Spicetify does **not** work with the Microsoft Store version of Spotify.
> Uninstall that one and install the desktop version from spotify.com first.

1. Install Spicetify by following the instructions at **spicetify.app**.
2. Copy the `spicetify\Ronin` folder into `%APPDATA%\spicetify\Themes\`
   (so you end up with `%APPDATA%\spicetify\Themes\Ronin\color.ini`).
3. In a terminal:

   ```
   spicetify config current_theme Ronin color_scheme Ronin
   spicetify apply
   ```

4. To undo: `spicetify restore`.
