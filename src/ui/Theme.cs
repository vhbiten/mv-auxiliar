using System.Drawing;
using System.Drawing.Drawing2D;

namespace SoulmvKit.UI
{
    // Paleta e tipografia central do app (marca Auxiliar).
    // Cores: #FFFFFF, #005691, #337AB7, #A8DADC, #003366.
    public static class Theme
    {
        public static readonly Color Primary      = ColorTranslator.FromHtml("#005691"); // azul principal (sidebar, botões)
        public static readonly Color PrimaryHover  = ColorTranslator.FromHtml("#003366"); // hover/realce escuro
        public static readonly Color Accent        = ColorTranslator.FromHtml("#337AB7"); // azul médio (hover de menu, texto secundário)
        public static readonly Color NavActive     = ColorTranslator.FromHtml("#003366"); // item de menu ativo
        public static readonly Color Light         = ColorTranslator.FromHtml("#A8DADC"); // ciano claro (bordas, texto esmaecido)

        public static readonly Color SidebarText  = Color.White;
        public static readonly Color SidebarMuted = ColorTranslator.FromHtml("#A8DADC");

        public static readonly Color AppBg     = ColorTranslator.FromHtml("#EAF2F6"); // fundo do login (tom claro da paleta)
        public static readonly Color ContentBg = ColorTranslator.FromHtml("#F4F8FB"); // fundo da área de conteúdo (cinza-azulado suave p/ destacar os cartões brancos)
        public static readonly Color CardBg    = Color.White;                          // superfície dos cartões
        public static readonly Color TextDark  = ColorTranslator.FromHtml("#003366");
        public static readonly Color TextMuted = ColorTranslator.FromHtml("#337AB7");
        public static readonly Color TextSoft  = ColorTranslator.FromHtml("#5B7C99"); // texto auxiliar/legenda (menos saturado)
        public static readonly Color Border    = ColorTranslator.FromHtml("#A8DADC");
        public static readonly Color BorderSoft = ColorTranslator.FromHtml("#D7E6EE"); // borda mais sutil para campos/cartões

        // Cores semânticas (status) + tons claros de fundo
        public static readonly Color Success     = ColorTranslator.FromHtml("#1F7A3D");
        public static readonly Color SuccessHover = ColorTranslator.FromHtml("#27914A");
        public static readonly Color Error       = ColorTranslator.FromHtml("#B03A2E");
        public static readonly Color Warning     = ColorTranslator.FromHtml("#9C6500");
        public static readonly Color InfoBg      = ColorTranslator.FromHtml("#E7F1F8");
        public static readonly Color SuccessBg   = ColorTranslator.FromHtml("#E3F3E8");
        public static readonly Color ErrorBg     = ColorTranslator.FromHtml("#FBE7E5");
        public static readonly Color WarningBg   = ColorTranslator.FromHtml("#FBF1DE");
        public static readonly Color GridZebra   = ColorTranslator.FromHtml("#F2F8FB");
        public static readonly Color DisabledFill = Color.FromArgb(255, 200, 216, 226);

        // Largura máxima da coluna de conteúdo (não esticar de ponta a ponta numa tela larga)
        public const int ContentMaxWidth = 1060;

        public static readonly Font H1       = new Font("Segoe UI", 16f, FontStyle.Bold);
        public static readonly Font H2       = new Font("Segoe UI", 13f, FontStyle.Bold);
        public static readonly Font H3       = new Font("Segoe UI Semibold", 11.5f, FontStyle.Regular); // título de cartão/seção
        public static readonly Font Body     = new Font("Segoe UI", 10f, FontStyle.Regular);
        public static readonly Font BodyBold = new Font("Segoe UI", 10f, FontStyle.Bold);
        public static readonly Font Input    = new Font("Segoe UI", 11f, FontStyle.Regular);          // campos (combo/numérico/texto)
        public static readonly Font Caption  = new Font("Segoe UI", 8.5f, FontStyle.Bold);            // micro-rótulo de campo
        public static readonly Font Small    = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        public static readonly Font Icon     = new Font("Segoe MDL2 Assets", 11f, FontStyle.Regular); // glifos de ação (Windows 10+)

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Sombra suave (faux) desenhada por trás de um retângulo arredondado:
        // anéis concêntricos com baixa opacidade, deslocados para baixo.
        public static void DrawSoftShadow(Graphics g, Rectangle r, int radius, int spread, int alpha)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = spread; i >= 1; i--)
            {
                var rr = new Rectangle(r.X - i, r.Y - i + 2, r.Width + i * 2, r.Height + i * 2);
                using (var path = RoundedRect(rr, radius + i))
                using (var pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), 1f))
                    g.DrawPath(pen, path);
            }
        }
    }
}
