using System.Drawing;
using System.IO;
using System.Reflection;

namespace SoulmvKit.UI
{
    // Carrega imagens/ícone embutidos no .exe (continua sendo arquivo único).
    public static class Assets
    {
        private static Image _logo;
        private static Image _favicon;
        private static Icon _appIcon;

        public static Image Logo
        {
            get { if (_logo == null) _logo = LoadImage("logo.png"); return _logo; }
        }

        public static Image Favicon
        {
            get { if (_favicon == null) _favicon = LoadImage("favicon.png"); return _favicon; }
        }

        public static Icon AppIcon
        {
            get
            {
                if (_appIcon == null)
                {
                    var s = Asm().GetManifestResourceStream("app.ico");
                    if (s != null) _appIcon = new Icon(s);
                }
                return _appIcon;
            }
        }

        private static Assembly Asm() { return Assembly.GetExecutingAssembly(); }

        private static Image LoadImage(string name)
        {
            var s = Asm().GetManifestResourceStream(name);
            if (s == null) return null;
            // copia para memória para o Image manter os dados após fechar o stream
            var ms = new MemoryStream();
            s.CopyTo(ms);
            s.Dispose();
            ms.Position = 0;
            return Image.FromStream(ms);
        }
    }
}
