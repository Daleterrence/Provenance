No fonts are bundled. The app uses the Windows system font, Segoe UI (see Font.Body in Themes/Palette.xaml).

To use a different font, add the TTFs to this folder, include them in the project as Resource, and point
Font.Body in Themes/Palette.xaml at them, for example
  <FontFamily x:Key="Font.Body">/Assets/Fonts/#Inter</FontFamily>
