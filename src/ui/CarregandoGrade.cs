using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Véu de "carregando" por cima de uma grade: a foto dela clareada, um spinner e a etapa
    // ("Lendo os lotes..."). Enquanto aparece, fica com o foco e por cima do mouse — a grade
    // embaixo não é mexida nem navegada (aba Atender, ao abrir a solicitação: até os lotes
    // chegarem, digitar era perder o que foi digitado). Quem usa desabilita a grade junto.
    public class CarregandoGrade : Control
    {
        private Bitmap _foto;
        private Control _alvo;
        private readonly Timer _giro;
        private int _angulo;
        private string _texto = "";

        public CarregandoGrade()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            this.TabStop = false;
            this.Visible = false;
            this.BackColor = Theme.CardBg;
            this.Cursor = Cursors.WaitCursor;
            _giro = new Timer();
            _giro.Interval = 33;
            _giro.Tick += new EventHandler(delegate
            {
                _angulo = (_angulo + 12) % 360;
                var r = Giro(); r.Inflate(Theme.S(4), Theme.S(4));
                Invalidate(r);
            });
        }

        public string Texto
        {
            get { return _texto; }
            set { _texto = value ?? ""; Invalidate(); }
        }

        // Cobre a grade com a foto dela e começa a girar.
        public void Mostrar(Control alvo, string texto)
        {
            _texto = texto ?? "";
            Fotografar(alvo);
            this.Visible = true;
            this.BringToFront();
            _giro.Start();
            this.Focus();   // tira o foco da grade (desabilitada) e do nº: as teclas param aqui
        }

        // Foto nova da grade (as linhas chegaram durante a carga).
        public void Fotografar(Control alvo)
        {
            _alvo = alvo;
            if (_foto != null) { _foto.Dispose(); _foto = null; }
            if (alvo.Width > 0 && alvo.Height > 0)
            {
                _foto = new Bitmap(alvo.Width, alvo.Height);
                try { alvo.DrawToBitmap(_foto, new Rectangle(0, 0, alvo.Width, alvo.Height)); }
                catch { _foto.Dispose(); _foto = null; }
            }
            Invalidate();
        }

        public void Esconder()
        {
            _giro.Stop();
            this.Visible = false;
            _alvo = null;
            if (_foto != null) { _foto.Dispose(); _foto = null; }
        }

        // Janela redimensionada durante a carga: foto nova depois que a grade se ajustar.
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!this.Visible || _alvo == null || !this.IsHandleCreated) return;
            this.BeginInvoke((MethodInvoker)delegate { if (this.Visible && !this.IsDisposed && _alvo != null) Fotografar(_alvo); });
        }

        // Teclas do véu não vão a lugar nenhum (setas, Tab e Enter incluídos).
        protected override bool IsInputKey(Keys keyData) { return true; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _giro.Dispose();
                if (_foto != null) { _foto.Dispose(); _foto = null; }
            }
            base.Dispose(disposing);
        }

        private int AlturaTexto { get { return TextRenderer.MeasureText("Ag", AtenderView.FonteGradeTitulo).Height; } }

        // Spinner + texto centralizados como um bloco.
        private Rectangle Giro()
        {
            int d = Theme.S(30);
            int bloco = d + Theme.S(12) + AlturaTexto;
            return new Rectangle((ClientSize.Width - d) / 2, (ClientSize.Height - bloco) / 2, d, d);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (_foto != null) g.DrawImageUnscaled(_foto, 0, 0); else g.Clear(BackColor);
            using (var veu = new SolidBrush(Color.FromArgb(200, Theme.CardBg))) g.FillRectangle(veu, ClientRectangle);

            var giro = Giro();
            int largTexto = Math.Min(ClientSize.Width - Theme.S(40), TextRenderer.MeasureText(_texto, AtenderView.FonteGradeTitulo).Width);
            var texto = new Rectangle((ClientSize.Width - largTexto) / 2, giro.Bottom + Theme.S(12), largTexto, AlturaTexto);

            // Cartão branco por trás do bloco: o spinner não se perde nas linhas clareadas.
            var cartao = Rectangle.Union(giro, texto);
            cartao.Inflate(Theme.S(28), Theme.S(18));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Arredondado(cartao, Theme.S(8)))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }

            using (var trilho = new Pen(Theme.GridLine, Theme.S(3))) g.DrawEllipse(trilho, giro);
            using (var arco = new Pen(Theme.Primary, Theme.S(3)))
            {
                arco.StartCap = LineCap.Round; arco.EndCap = LineCap.Round;
                g.DrawArc(arco, giro, _angulo, 100);
            }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, _texto, AtenderView.FonteGradeTitulo, texto, Theme.TextDark,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static GraphicsPath Arredondado(Rectangle r, int raio)
        {
            int d = raio * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
