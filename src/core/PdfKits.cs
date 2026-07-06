using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Extrai texto de um PDF simples (streams FlateDecode + operadores Tj) e interpreta o
    // "Relatório de Kits por Produto" (R_KIT_PROD) em uma lista de KitEstoque.
    // Sem libs externas: usa DeflateStream (System.dll) para inflar os streams zlib.
    public static class PdfKits
    {
        // ---- Extração de texto ----
        public static List<string> ExtrairTextos(byte[] pdf)
        {
            return ExtrairTj(ConteudoInflado(pdf));
        }

        // Concatena todos os streams FlateDecode inflados (conteúdo das páginas).
        public static string ConteudoInflado(byte[] pdf)
        {
            var content = new StringBuilder();
            byte[] sk = Encoding.ASCII.GetBytes("stream");
            byte[] ek = Encoding.ASCII.GetBytes("endstream");
            int i = 0;
            while (true)
            {
                int sIdx = IndexOf(pdf, sk, i);
                if (sIdx < 0) break;
                int dataStart = sIdx + sk.Length;
                if (dataStart < pdf.Length && pdf[dataStart] == 0x0D) dataStart++;
                if (dataStart < pdf.Length && pdf[dataStart] == 0x0A) dataStart++;
                int eIdx = IndexOf(pdf, ek, dataStart);
                if (eIdx < 0) break;
                int len = eIdx - dataStart;
                string inflated = Inflar(pdf, dataStart, len);
                if (inflated != null) content.Append(inflated).Append('\n');
                i = eIdx + ek.Length;
            }
            return content.ToString();
        }

        private static string Inflar(byte[] data, int off, int len)
        {
            if (len < 3) return null;
            // zlib = 2 bytes de cabeçalho + deflate cru; DeflateStream quer deflate cru.
            string r = TryInflate(data, off + 2, len - 2);
            if (r != null) return r;
            return TryInflate(data, off, len); // fallback: deflate sem cabeçalho
        }

        private static string TryInflate(byte[] data, int off, int len)
        {
            try
            {
                using (var ms = new MemoryStream(data, off, len))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var os = new MemoryStream())
                {
                    byte[] b = new byte[8192]; int n;
                    while ((n = ds.Read(b, 0, b.Length)) > 0) os.Write(b, 0, n);
                    return Encoding.GetEncoding(1252).GetString(os.ToArray());
                }
            }
            catch { return null; }
        }

        private static int IndexOf(byte[] hay, byte[] needle, int start)
        {
            for (int i = start; i <= hay.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        private static List<string> ExtrairTj(string content)
        {
            var res = new List<string>();
            var rx = new Regex(@"\(((?:[^()\\]|\\.)*)\)\s*Tj");
            foreach (Match m in rx.Matches(content))
                res.Add(DecodePdfString(m.Groups[1].Value));
            return res;
        }

        public static string DecodePdfString(string raw)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char n = raw[++i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '(': sb.Append('('); break;
                        case ')': sb.Append(')'); break;
                        case '\\': sb.Append('\\'); break;
                        default:
                            if (n >= '0' && n <= '7')
                            {
                                int val = n - '0', cnt = 1;
                                while (cnt < 3 && i + 1 < raw.Length && raw[i + 1] >= '0' && raw[i + 1] <= '7')
                                { val = val * 8 + (raw[++i] - '0'); cnt++; }
                                sb.Append((char)val);
                            }
                            else sb.Append(n);
                            break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- Interpretação do relatório R_KIT_PROD ----
        // Âncora = token "Movimento": antes dele vêm [cód. do kit][nome]; depois [fórmula];
        // em seguida as linhas de unidade (Estq, Cód. de Barras 13 díg., Cód. do kit).
        // Kits que se repetem (relatório de várias páginas) são agregados pelo cód. do kit.
        public static List<KitEstoque> Interpretar(byte[] pdf)
        {
            var toks = ExtrairTextos(pdf);
            var ordem = new List<int>();
            var mapa = new Dictionary<int, KitEstoque>();
            var barcode = new Regex(@"^\d{12,14}$");

            for (int k = 0; k < toks.Count; k++)
            {
                if (toks[k] != "Movimento") continue;
                if (k < 2) continue;

                int cdKit; if (!int.TryParse(toks[k - 2], out cdKit)) continue;
                string nome = toks[k - 1];
                int formula = 0; if (k + 1 < toks.Count) int.TryParse(toks[k + 1], out formula);

                KitEstoque kit;
                if (!mapa.TryGetValue(cdKit, out kit))
                {
                    kit = new KitEstoque();
                    kit.CdKit = cdKit; kit.Nome = nome; kit.Formula = formula;
                    mapa[cdKit] = kit; ordem.Add(cdKit);
                }

                // varre as unidades até a próxima âncora "Movimento" (ou fim)
                for (int j = k + 2; j < toks.Count; j++)
                {
                    if (toks[j] == "Movimento") break;
                    if (barcode.IsMatch(toks[j]))
                    {
                        var u = new KitUnidade();
                        u.CodBarras = toks[j];
                        u.CodKit = (j + 1 < toks.Count) ? toks[j + 1] : "";
                        kit.Unidades.Add(u);
                    }
                }
            }

            var res = new List<KitEstoque>();
            foreach (int cd in ordem) res.Add(mapa[cd]);
            return res;
        }
    }
}
