using System.Windows.Media;

namespace PdvPadaria
{
    /// <summary>
    /// Paleta central do code-behind. Espelha os tokens de cor do App.xaml
    /// (design system). Use estes membros em vez de criar SolidColorBrush
    /// inline com Color.FromRgb, para manter uma fonte unica de cor.
    /// Brushes sao congelados (Freeze) para performance e reuso seguro.
    /// </summary>
    public static class AppColors
    {
        // ===== Cores (struct Color) — para casos que precisam de Color, ex: ternarios/gradientes =====
        public static readonly Color BgBaseColor      = Color.FromRgb(6, 6, 8);        // #060608
        public static readonly Color SurfaceColor     = Color.FromRgb(20, 20, 25);     // #141419
        public static readonly Color BorderSoftColor  = Color.FromRgb(38, 38, 46);     // #26262E
        public static readonly Color AccentColor      = Color.FromRgb(245, 158, 11);   // #F59E0B
        public static readonly Color TextPrimaryColor = Color.FromRgb(248, 250, 252);  // #F8FAFC
        public static readonly Color TextMutedColor   = Color.FromRgb(148, 163, 184);  // #94A3B8
        public static readonly Color DangerColor      = Color.FromRgb(239, 68, 68);    // #EF4444

        // ===== Brushes (congelados) — uso direto em Foreground/Background/BorderBrush =====
        public static readonly SolidColorBrush BgBase      = Frozen(BgBaseColor);
        public static readonly SolidColorBrush Surface     = Frozen(SurfaceColor);
        public static readonly SolidColorBrush BorderSoft  = Frozen(BorderSoftColor);
        public static readonly SolidColorBrush Accent      = Frozen(AccentColor);
        public static readonly SolidColorBrush TextPrimary = Frozen(TextPrimaryColor);
        public static readonly SolidColorBrush TextMuted   = Frozen(TextMutedColor);
        public static readonly SolidColorBrush Danger      = Frozen(DangerColor);

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
