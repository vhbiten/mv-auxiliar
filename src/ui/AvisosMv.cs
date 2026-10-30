using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Notificações no canto inferior direito, como as "Mensagem ao Usuário" do MV Soul (é onde os
    // atendentes estão acostumados a olhar). Até 3 empilhadas, a mais nova embaixo. Informação e
    // sucesso somem sozinhos; aviso e erro ficam até fechar no ✕ (ou até abrir outra
    // solicitação); "aguarde" é um só, atualizado no lugar, e some quando a operação termina.
    // Mesmo vocabulário do StatusBanner: SetState(tipo, texto).
    public class AvisosMv
    {
        private const int Maximo = 3;
        private readonly Control _host;
        private readonly List<AvisoMv> _avisos = new List<AvisoMv>();
        private AvisoMv _aguarde;

        // Último texto mostrado (os testes leem daqui).
        public string Ultimo = "";

        public AvisosMv(Control host)
        {
            _host = host;
            _host.Resize += new EventHandler(delegate { Reposicionar(); });
        }

        public AvisoMv SetState(StatusKind tipo, string texto) { return SetState(tipo, texto, null); }

        // Devolve o cartão (para quem quiser fechá-lo antes, ex.: o aviso de inatividade).
        public AvisoMv SetState(StatusKind tipo, string texto, string titulo)
        {
            if (_host.IsDisposed) return null;
            Ultimo = texto ?? "";
            if (tipo == StatusKind.Busy)
            {
                if (_aguarde == null)
                {
                    _aguarde = new AvisoMv(tipo, titulo ?? "Aguarde", texto, Fechou);
                    _host.Controls.Add(_aguarde);
                }
                else _aguarde.Atualizar(titulo ?? "Aguarde", texto);
                Reposicionar();
                return _aguarde;
            }
            Livre();
            var a = new AvisoMv(tipo, titulo ?? TituloPadrao(tipo), texto, Fechou);
            _avisos.Add(a);
            _host.Controls.Add(a);
            while (_avisos.Count > Maximo) Remover(_avisos[0]);
            Reposicionar();
            return a;
        }

        public void Fechar(AvisoMv a) { if (a != null && _avisos.Contains(a)) Remover(a); }

        private static string TituloPadrao(StatusKind tipo)
        {
            switch (tipo)
            {
                case StatusKind.Success: return "Concluído";
                case StatusKind.Warning: return "Atenção";
                case StatusKind.Error: return "Não foi possível";
                default: return "Informação";
            }
        }

        // Fim da operação: some o "aguarde".
        public void Livre()
        {
            if (_aguarde == null) return;
            var a = _aguarde; _aguarde = null;
            _host.Controls.Remove(a); a.Dispose();
            Reposicionar();
        }

        // Outra solicitação: os avisos da anterior não valem mais.
        public void Limpar()
        {
            foreach (var a in _avisos.ToArray()) Remover(a);
            Livre();
        }

        private void Fechou(AvisoMv a) { if (a == _aguarde) Livre(); else Remover(a); }

        private void Remover(AvisoMv a)
        {
            _avisos.Remove(a);
            if (!a.IsDisposed) { _host.Controls.Remove(a); a.Dispose(); }
            Reposicionar();
        }

        private void Reposicionar()
        {
            if (_host.IsDisposed) return;
            int y = _host.ClientSize.Height - Theme.S(10);
            int x = _host.ClientSize.Width - Theme.S(12);
            var pilha = new List<AvisoMv>();
            if (_aguarde != null) pilha.Add(_aguarde);
            for (int i = _avisos.Count - 1; i >= 0; i--) pilha.Add(_avisos[i]);
            foreach (var a in pilha)
            {
                y -= a.Height;
                a.Location = new Point(x - a.Width, y);
                a.BringToFront();
                y -= Theme.S(8);
            }
        }
    }

    // Um cartão de notificação: barra de cor à esquerda, glifo, título, texto e ✕.
    public class AvisoMv : Control
    {
        public static readonly Font FonteTitulo = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font FonteTexto = new Font("Segoe UI", 9f, FontStyle.Regular);
        private const TextFormatFlags Quebra = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;

        private readonly StatusKind _tipo;
        private readonly Action<AvisoMv> _fechou;
        private string _titulo, _texto;
        private Timer _relogio;
        private int _passo;
        private bool _sobreX;

        public AvisoMv(StatusKind tipo, string titulo, string texto, Action<AvisoMv> fechou)
        {
            _tipo = tipo; _fechou = fechou;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = Theme.S(360);
            Cursor = Cursors.Default;
            Atualizar(titulo, texto);
            // Informação some em 5 s, sucesso em 8 s; aguarde anima; aviso e erro ficam.
            if (tipo == StatusKind.Info || tipo == StatusKind.Success)
            {
                _relogio = new Timer();
                _relogio.Interval = tipo == StatusKind.Info ? 5000 : 8000;
                _relogio.Tick += new EventHandler(delegate { _relogio.Stop(); _fechou(this); });
                _relogio.Start();
            }
            else if (tipo == StatusKind.Busy)
            {
                _relogio = new Timer();
                _relogio.Interval = 350;
                _relogio.Tick += new EventHandler(delegate { _passo = (_passo + 1) % 3; Invalidate(); });
                _relogio.Start();
            }
        }

        public void Atualizar(string titulo, string texto)
        {
            _titulo = titulo ?? ""; _texto = texto ?? "";
            int larguraTexto = Width - Theme.S(52) - Theme.S(30);
            int h = TextRenderer.MeasureText(_texto, FonteTexto, new Size(larguraTexto, 0), Quebra).Height;
            Height = Theme.S(12) + FonteTitulo.Height + Theme.S(4) + h + Theme.S(12);
            Invalidate();
        }

        private Rectangle AreaX { get { return new Rectangle(Width - Theme.S(28), Theme.S(4), Theme.S(24), Theme.S(22)); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool sobre = _tipo != StatusKind.Busy && AreaX.Contains(e.Location);
            if (sobre != _sobreX) { _sobreX = sobre; Cursor = sobre ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_sobreX) { _sobreX = false; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (_tipo != StatusKind.Busy && AreaX.Contains(e.Location)) _fechou(this);
            base.OnMouseClick(e);
        }

        public static void Paleta(StatusKind tipo, int passo, out Color forte, out Color fundo, out string glifo)
        {
            switch (tipo)
            {
                case StatusKind.Success: forte = Theme.Success; fundo = Theme.SuccessBg; glifo = "✓"; break;
                case StatusKind.Warning: forte = Theme.Warning; fundo = Theme.WarningBg; glifo = "!"; break;
                case StatusKind.Error: forte = Theme.Error; fundo = Theme.ErrorBg; glifo = "×"; break;
                case StatusKind.Busy: forte = Theme.Primary; fundo = Theme.InfoBg; glifo = passo == 0 ? "·" : (passo == 1 ? "··" : "···"); break;
                default: forte = Theme.Primary; fundo = Theme.InfoBg; glifo = "i"; break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color forte, fundo; string glifo;
            Paleta(_tipo, _passo, out forte, out fundo, out glifo);
            g.Clear(Color.White);
            using (var b = new SolidBrush(fundo)) g.FillRectangle(b, 0, 0, Theme.S(40), Height);
            using (var b = new SolidBrush(forte)) g.FillRectangle(b, 0, 0, Theme.S(4), Height);
            using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var f = new Font("Segoe UI", 12f, FontStyle.Bold))
                TextRenderer.DrawText(g, glifo, f, new Rectangle(Theme.S(4), Theme.S(8), Theme.S(36), Theme.S(24)), forte,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int x = Theme.S(52), y = Theme.S(12), w = Width - x - Theme.S(30);
            TextRenderer.DrawText(g, _titulo, FonteTitulo, new Rectangle(x, y, w, FonteTitulo.Height), Theme.TextDark, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            y += FonteTitulo.Height + Theme.S(4);
            TextRenderer.DrawText(g, _texto, FonteTexto, new Rectangle(x, y, w, Height - y - Theme.S(8)), Theme.TextDark, Quebra);
            if (_tipo != StatusKind.Busy)
                TextRenderer.DrawText(g, "✕", FonteTexto, AreaX, _sobreX ? Theme.Error : Theme.TextSoft,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _relogio != null) { _relogio.Dispose(); _relogio = null; }
            base.Dispose(disposing);
        }
    }

    // Pergunta (Sim/Não) no canto inferior direito, no mesmo jeito das notificações e das
    // "Mensagem ao Usuário" do MV. Modal: Enter = botão em destaque, Esc = o outro.
    public class PerguntaMv : Form
    {
        private readonly string _titulo, _texto;
        private readonly PrimaryButton _sim, _nao;
        private readonly Form _dono;

        private PerguntaMv(Form dono, string titulo, string texto, string sim, string nao, bool padraoSim)
        {
            _dono = dono; _titulo = titulo ?? ""; _texto = texto ?? "";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.White;
            KeyPreview = true;
            DoubleBuffered = true;
            Width = Theme.S(380);

            _nao = new PrimaryButton(); _nao.Text = nao; _nao.Ghost = true;
            _sim = new PrimaryButton(); _sim.Text = sim;
            foreach (var b in new[] { _nao, _sim })
            {
                b.Height = Theme.S(30);
                b.Width = Math.Max(Theme.S(96), TextRenderer.MeasureText(b.Text, Theme.BodyBold).Width + Theme.S(28));
                Controls.Add(b);
            }
            _sim.Click += new EventHandler(delegate { DialogResult = DialogResult.OK; Close(); });
            _nao.Click += new EventHandler(delegate { DialogResult = DialogResult.Cancel; Close(); });
            AcceptButton = padraoSim ? (IButtonControl)_sim : _nao;
            CancelButton = _nao;
            // Botão em destaque cheio; o outro só com contorno (o padrão é o seguro, quando é o Não).
            if (!padraoSim) { _sim.Ghost = true; _nao.Ghost = false; }

            int larguraTexto = Width - Theme.S(52) - Theme.S(16);
            int h = TextRenderer.MeasureText(_texto, AvisoMv.FonteTexto, new Size(larguraTexto, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            int yBotoes = Theme.S(12) + AvisoMv.FonteTitulo.Height + Theme.S(6) + h + Theme.S(12);
            Height = yBotoes + _sim.Height + Theme.S(12);
            _sim.Location = new Point(Width - Theme.S(12) - _sim.Width, yBotoes);
            _nao.Location = new Point(_sim.Left - Theme.S(8) - _nao.Width, yBotoes);
        }

        // true = respondeu o primeiro botão (sim).
        public static bool Perguntar(Control origem, string titulo, string texto, string sim, string nao, bool padraoSim)
        {
            Form dono = origem != null ? origem.FindForm() : null;
            using (var p = new PerguntaMv(dono, titulo, texto, sim, nao, padraoSim))
                return p.ShowDialog(dono) == DialogResult.OK;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle area = _dono != null && _dono.Visible ? _dono.RectangleToScreen(_dono.ClientRectangle) : Screen.FromControl(this).WorkingArea;
            Location = new Point(area.Right - Width - Theme.S(12), area.Bottom - Height - Theme.S(10));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ((Control)AcceptButton).Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!e.Cancel && _dono != null) { try { _dono.Activate(); } catch { } }
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000 /* CS_DROPSHADOW */; return cp; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Theme.WarningBg)) g.FillRectangle(b, 0, 0, Theme.S(40), Height);
            using (var b = new SolidBrush(Theme.Warning)) g.FillRectangle(b, 0, 0, Theme.S(4), Height);
            using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var f = new Font("Segoe UI", 12f, FontStyle.Bold))
                TextRenderer.DrawText(g, "?", f, new Rectangle(Theme.S(4), Theme.S(8), Theme.S(36), Theme.S(24)), Theme.Warning,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int x = Theme.S(52), y = Theme.S(12), w = Width - x - Theme.S(16);
            TextRenderer.DrawText(g, _titulo, AvisoMv.FonteTitulo, new Rectangle(x, y, w, AvisoMv.FonteTitulo.Height), Theme.TextDark, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            y += AvisoMv.FonteTitulo.Height + Theme.S(6);
            TextRenderer.DrawText(g, _texto, AvisoMv.FonteTexto, new Rectangle(x, y, w, _sim.Top - y), Theme.TextDark, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }
}
