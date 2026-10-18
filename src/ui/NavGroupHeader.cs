using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Título de grupo recolhível da barra lateral (ex.: "KITS"). Clique, Enter ou Espaço
    // abre e fecha o grupo; a seta à direita mostra o estado.
    //
    // Visual na paleta da marca: faixa um degrau mais escura que a sidebar (PrimaryMid),
    // ícone + texto em ciano claro (Light) e hover no mesmo Accent dos itens. Os itens do
    // grupo ficam alinhados ao texto do título (recuo do ícone), o que mostra a hierarquia
    // sem precisar de linha ou barra. Com o grupo fechado e o módulo ativo dentro dele,
    // ícone e texto ficam brancos — o usuário continua sabendo onde está.
    public class NavGroupHeader : Panel
    {
        private static readonly Font FonteSeta = new Font("Segoe MDL2 Assets", 8f, FontStyle.Regular);
        private static readonly Font FonteIcone = new Font("Segoe MDL2 Assets", 11f, FontStyle.Regular);

        // Recuo do texto do título; o NavButton usa o mesmo valor para alinhar os itens.
        public const int RecuoTexto = 40;

        private readonly string _text;
        private readonly string _glifo;
        private bool _expanded = true;
        private bool _contemAtivo;
        private bool _hover;

        public event EventHandler Toggled;

        // Ícone desenhado por código (para o que a Segoe MDL2 não tem, ex.: cápsula).
        private readonly Action<Graphics, Rectangle, Color> _desenho;

        // glifo: caractere da fonte Segoe MDL2 Assets (ex.: "" = caixa).
        public NavGroupHeader(string text, string glifo) : this(text, glifo, null) { }

        // desenho: pinta o ícone na área dada, na cor dada (substitui o glifo).
        public NavGroupHeader(string text, Action<Graphics, Rectangle, Color> desenho) : this(text, null, desenho) { }

        private NavGroupHeader(string text, string glifo, Action<Graphics, Rectangle, Color> desenho)
        {
            _desenho = desenho;
            _text = text.ToUpperInvariant();
            _glifo = glifo;
            this.Width = Theme.S(210);
            this.Height = Theme.S(32);
            this.Margin = new Padding(0, Theme.S(4), 0, 0);
            this.Cursor = Cursors.Hand;
            this.BackColor = Theme.PrimaryMid;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.Selectable, true);   // alcançável por Tab
            this.TabStop = true;

            this.Click += new EventHandler(delegate { Alternar(); });
            this.MouseEnter += new EventHandler(delegate { _hover = true; this.Invalidate(); });
            this.MouseLeave += new EventHandler(delegate { _hover = false; this.Invalidate(); });
        }

        public bool Expanded
        {
            get { return _expanded; }
            set { _expanded = value; this.Invalidate(); }
        }

        // O módulo ativo está dentro deste grupo (realça o título quando fechado).
        public bool ContemAtivo
        {
            get { return _contemAtivo; }
            set { _contemAtivo = value; this.Invalidate(); }
        }

        // O primeiro grupo encosta no topo da lista (sem o respiro dos demais).
        public bool Primeiro
        {
            set { this.Margin = new Padding(0, value ? 0 : Theme.S(4), 0, 0); }
        }

        // Cápsula de remédio inclinada a 45°, no traço dos glifos MDL2: contorno de 1 px
        // (escalado pelo DPI) e a metade de cima preenchida.
        public static void DesenharCapsula(Graphics g, Rectangle area, Color cor)
        {
            var estado = g.Save();
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float lado = Math.Min(area.Width, area.Height) * 0.78f;
            if (lado > Theme.S(17)) lado = Theme.S(17);
            float larg = lado * 0.42f;
            float cx = area.X + area.Width / 2f, cy = area.Y + area.Height / 2f;
            g.TranslateTransform(cx, cy);
            g.RotateTransform(45f);
            var rect = new RectangleF(-larg / 2f, -lado / 2f, larg, lado);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float d = larg;   // raio = metade da largura: pontas totalmente redondas
                path.AddArc(rect.X, rect.Y, d, d, 180, 180);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 0, 180);
                path.CloseFigure();
                // metade de cima cheia
                var reg = g.Clip;
                g.SetClip(new RectangleF(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height / 2f + 1));
                using (var br = new SolidBrush(cor)) g.FillPath(br, path);
                g.Clip = reg;
                using (var pen = new Pen(cor, Math.Max(1f, Theme.Escala * 1.1f)))
                {
                    g.DrawPath(pen, path);
                    g.DrawLine(pen, rect.X, 0, rect.Right, 0);
                }
            }
            g.Restore(estado);
        }

        private void Alternar()
        {
            if (Toggled != null) Toggled(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color fundo = _hover ? Theme.Accent : Theme.PrimaryMid;
            using (var br = new SolidBrush(fundo))
                e.Graphics.FillRectangle(br, this.ClientRectangle);

            bool realce = _contemAtivo && !_expanded;
            Color cor = (realce || _hover) ? Color.White : Theme.Light;
            TextFormatFlags centro = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

            var areaIcone = new Rectangle(Theme.S(12), 0, Theme.S(22), this.Height);
            if (_desenho != null) _desenho(e.Graphics, areaIcone, cor);
            else if (!string.IsNullOrEmpty(_glifo))
                TextRenderer.DrawText(e.Graphics, _glifo, FonteIcone, areaIcone, cor, centro);

            var area = new Rectangle(Theme.S(RecuoTexto), 0, this.Width - Theme.S(RecuoTexto + 32), this.Height);
            TextRenderer.DrawText(e.Graphics, _text, Theme.Caption, area, cor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // Seta: para baixo = aberto, para a direita = fechado.
            string seta = _expanded ? "" : "";
            var areaSeta = new Rectangle(this.Width - Theme.S(32), 0, Theme.S(22), this.Height);
            TextRenderer.DrawText(e.Graphics, seta, FonteSeta, areaSeta, cor, centro);

            // Mesma regra do NavButton: anel de foco só na navegação por teclado.
            if (this.Focused && this.ShowFocusCues)
            {
                using (var pen = new Pen(Color.White, 1f))
                    e.Graphics.DrawRectangle(pen, 2, 2, this.Width - 5, this.Height - 5);
            }
        }

        protected override void OnGotFocus(EventArgs e) { this.Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { this.Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            this.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                Alternar();
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
