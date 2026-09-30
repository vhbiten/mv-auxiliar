using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Um produto do "Relatório de Produtos Críticos - Analítico" (R_PROD_CRIT):
    // parâmetros de reposição (mín/máx/ponto de pedido) + situação atual do estoque.
    public class ProdCritItem
    {
        public int Codigo;
        public string Nome;              // sem o sufixo "(INATIVO)"
        public bool Inativo;
        public string Unidade;
        public int Especie;              // número da seção "Especie:" corrente (0 = desconhecida)
        public decimal EstoqueMinimo;
        public decimal EstoqueMaximo;
        public decimal PontoPedido;
        public decimal EstoqueAtual;
        public decimal CustoMedio;
        public decimal ConsumoMedio;
        public decimal DuracaoDias;
        public string Fornecedor;        // fornecedor da última entrada ("" se não houver)
        public DateTime? UltimaEntrada;  // null = nunca entrou
    }

    // Interpreta o PDF do R_PROD_CRIT. Diferente dos outros relatórios do repo, aqui a
    // ordem SEQUENCIAL dos tokens não basta: o Oracle Reports emite as células de cada
    // linha em ordem reversa e simplesmente OMITE campos vazios (produto sem última
    // entrada não emite data nem fornecedor), então contar tokens desalinha tudo.
    // A saída é extrair cada texto com sua coordenada (x,y) — todo Tj deste gerador vem
    // precedido de um Tm — e decidir a coluna pelo x e a linha pelo y.
    //
    // Layout descoberto no PDF real (x = borda esquerda do texto; numéricos são
    // alinhados à direita, então o x diminui conforme o valor alarga):
    //   [4..46,6)    Código (inteiro, alinhado à direita terminando em ~46,5)
    //   [46,6..142)  Nome (alinhado em x=47; QUEBRA em várias linhas 9,2pt abaixo;
    //                o sufixo "(INATIVO)" vem como linha própria)
    //   [142..200)   Unidade (x=143, truncada pelo gerador: "COMPRIMI", "FA C/200M")
    //   [200..250,5) Estoque Máximo         [250,5..310,5) Estoque Mínimo
    //   [310,5..370,5) Estoque Atual        [370,5..442,5) Custo Médio
    //   [442,5..507) Dt. Última Entrada (dd/MM/yyyy)
    //   [507..610)   Fornecedor (x=509, truncado: "VIVA PRODUTOS HOSP")
    //   [610..652,5) Duração Dias           [652,5..717,5) Consumo Médio
    //   [717,5..790) Ponto de Pedido
    //
    // Seções: a linha "Especie: <n> <NOME>" (rótulo em x≈43, número em x≈99, nome em
    // x≈109) define a espécie corrente; "Classe:"/"Subclasse:" idem e são ignoradas.
    // Linhas de total ("Total da Classe:"/"Total da Especie:", valores em x≈723-737) e
    // cabeçalhos/rodapés de página não têm código na coluna própria e são descartados.
    public static class PdfProdCrit
    {
        // Fronteiras de coluna (ver tabela acima). Constantes p/ não espalhar mágica.
        private const double XCodMin = 4.0, XNome = 46.6, XUnid = 142.0, XEstMax = 200.0,
            XEstMin = 250.5, XEstAtu = 310.5, XCusto = 370.5, XData = 442.5,
            XForn = 507.0, XDur = 610.0, XCons = 652.5, XPonto = 717.5, XFim = 790.0;

        public static List<ProdCritItem> Interpretar(byte[] pdf)
        {
            var ordem = new List<int>();
            var mapa = new Dictionary<int, ProdCritItem>();
            // Nome acumula por item (a quebra de linha chega como token separado).
            var nomes = new Dictionary<ProdCritItem, List<string>>();

            int especie = 0;
            foreach (string pagina in Paginas(pdf))
            {
                var linhas = AgruparLinhas(ExtrairPosicionado(pagina));

                // Item "aberto" recebe linhas de continuação do nome (só na mesma página:
                // o Oracle não quebra a linha do produto entre páginas).
                ProdCritItem aberto = null;
                double yNome = 0;

                foreach (var linha in linhas)
                {
                    try
                    {
                        // Linha de seção? "Especie:" atualiza a espécie corrente; Classe e
                        // Subclasse só fecham o item aberto.
                        bool secao = false;
                        foreach (var t in linha)
                        {
                            string s = t.Texto.Trim();
                            if (s == "Especie:" || s == "Classe:" || s == "Subclasse:")
                            {
                                secao = true;
                                if (s == "Especie:")
                                    foreach (var n in linha)
                                    {
                                        int e;
                                        if (n.X > 60 && n.X < 108 && int.TryParse(n.Texto.Trim(), out e))
                                        { especie = e; break; }
                                    }
                                break;
                            }
                        }
                        if (secao) { aberto = null; continue; }

                        // Linha de produto? Âncora = inteiro na coluna do código.
                        int codigo = 0;
                        foreach (var t in linha)
                            if (t.X >= XCodMin && t.X < XNome && int.TryParse(t.Texto.Trim(), out codigo) && codigo > 0)
                                break;
                            else codigo = 0;

                        if (codigo > 0)
                        {
                            var item = MontarItem(codigo, especie, linha, nomes);
                            // Dedup por código: cabeçalho repetido em relatório multipágina.
                            // Ativo x (INATIVO) têm códigos diferentes, então ambos ficam.
                            if (!mapa.ContainsKey(codigo)) { mapa[codigo] = item; ordem.Add(codigo); }
                            else
                            {
                                // Vale a 1ª ocorrência. Se o MV um dia repetir código com
                                // cadastro DIVERGENTE, o log denuncia a premissa quebrada.
                                var v = mapa[codigo];
                                if (v.EstoqueMinimo != item.EstoqueMinimo || v.PontoPedido != item.PontoPedido || v.Inativo != item.Inativo)
                                    Logger.Log("PdfProdCrit: código " + codigo + " repetido com cadastro divergente — mantida a 1ª ocorrência.");
                            }
                            aberto = item; yNome = linha[0].Y;
                            continue;
                        }

                        // Continuação do nome: todos os tokens na coluna do nome, logo
                        // abaixo (passo de ~9,2pt) da última linha de nome do item aberto.
                        if (aberto != null && yNome - linha[0].Y <= 12.5)
                        {
                            bool soNome = true;
                            foreach (var t in linha)
                                if (t.X < XNome || t.X >= XUnid) { soNome = false; break; }
                            if (soNome)
                            {
                                foreach (var t in linha)
                                    if (t.Texto.Trim().Length > 0) nomes[aberto].Add(t.Texto.Trim());
                                yNome = linha[0].Y;
                                continue;
                            }
                        }

                        aberto = null;  // total, cabeçalho, rodapé... fecha o item
                    }
                    catch { aberto = null; }  // linha malformada nunca derruba o parse
                }
            }

            var res = new List<ProdCritItem>();
            foreach (int cd in ordem)
            {
                var item = mapa[cd];
                FecharNome(item, nomes[item]);
                res.Add(item);
            }
            return res;
        }

        private static ProdCritItem MontarItem(int codigo, int especie, List<TextoPos> linha,
            Dictionary<ProdCritItem, List<string>> nomes)
        {
            var item = new ProdCritItem();
            item.Codigo = codigo;
            item.Especie = especie;
            item.Nome = ""; item.Unidade = ""; item.Fornecedor = "";
            nomes[item] = new List<string>();

            foreach (var t in linha)
            {
                string s = t.Texto.Trim();
                if (s.Length == 0) continue;
                double x = t.X;
                decimal v;
                if (x < XNome) continue;                        // o próprio código
                else if (x < XUnid) nomes[item].Add(s);
                else if (x < XEstMax) { if (item.Unidade.Length == 0) item.Unidade = s; }
                else if (x < XEstMin) { if (Numero(s, out v)) item.EstoqueMaximo = v; }
                else if (x < XEstAtu) { if (Numero(s, out v)) item.EstoqueMinimo = v; }
                else if (x < XCusto) { if (Numero(s, out v)) item.EstoqueAtual = v; }
                else if (x < XData) { if (Numero(s, out v)) item.CustoMedio = v; }
                else if (x < XForn)
                {
                    DateTime dt;
                    if (DateTime.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out dt)) item.UltimaEntrada = dt;
                }
                else if (x < XDur)
                    item.Fornecedor = item.Fornecedor.Length == 0 ? s : item.Fornecedor + " " + s;
                else if (x < XCons) { if (Numero(s, out v)) item.DuracaoDias = v; }
                else if (x < XPonto) { if (Numero(s, out v)) item.ConsumoMedio = v; }
                else if (x < XFim) { if (Numero(s, out v)) item.PontoPedido = v; }
            }
            return item;
        }

        // Junta as partes do nome e destaca o "(INATIVO)" (o MV anexa como token próprio).
        private static void FecharNome(ProdCritItem item, List<string> partes)
        {
            string nome = Regex.Replace(string.Join(" ", partes.ToArray()), @"\s+", " ").Trim();
            if (Regex.IsMatch(nome, @"\(\s*INATIVO\s*\)\s*$", RegexOptions.IgnoreCase))
            {
                item.Inativo = true;
                nome = Regex.Replace(nome, @"\s*\(\s*INATIVO\s*\)\s*$", "", RegexOptions.IgnoreCase).Trim();
            }
            item.Nome = nome;
        }

        // Número pt-BR da máscara Oracle: "300,00", "8640,00", "1.377,00", "520,4218";
        // negativo com sinal à esquerda OU à direita ("12,00-"), como no R_MOVI_ESTOQ.
        private static bool Numero(string s, out decimal v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (!Regex.IsMatch(s, "^-?(\\d{1,3}(\\.\\d{3})+|\\d+),\\d{1,4}-?$")) return false;
            bool neg = s.StartsWith("-") || s.EndsWith("-");
            s = s.Trim('-');
            if (!decimal.TryParse(s.Replace(".", "").Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out v)) return false;
            if (neg) v = -v;
            return true;
        }

        // ---- Extração posicional ----

        private class TextoPos
        {
            public double X, Y;
            public string Texto;
        }

        // Uma linha visual = tokens com o mesmo y (tolerância 2pt: rótulo e valor dos
        // totais divergem 1pt). Linhas devolvidas de cima para baixo, tokens por x.
        private static List<List<TextoPos>> AgruparLinhas(List<TextoPos> toks)
        {
            toks.Sort(delegate(TextoPos a, TextoPos b) { return b.Y.CompareTo(a.Y); });
            var linhas = new List<List<TextoPos>>();
            List<TextoPos> atual = null;
            double yRef = 0;
            foreach (var t in toks)
            {
                if (atual == null || yRef - t.Y > 2.0)
                {
                    atual = new List<TextoPos>();
                    linhas.Add(atual);
                    yRef = t.Y;
                }
                atual.Add(t);
            }
            foreach (var l in linhas)
                l.Sort(delegate(TextoPos a, TextoPos b) { return a.X.CompareTo(b.X); });
            return linhas;
        }

        // Rastreia os operadores de posicionamento (Tm/Td/TD/TL/T*) para saber onde cada
        // (...)Tj foi desenhado. Neste gerador todo Tj vem precedido de Tm, mas os demais
        // operadores ficam cobertos por segurança. Matriz tratada só como translação:
        // as páginas do Oracle Reports não rotacionam nem escalam o texto.
        private static List<TextoPos> ExtrairPosicionado(string content)
        {
            var res = new List<TextoPos>();
            double x = 0, y = 0, tl = 0;
            var nums = new List<double>();
            int i = 0, n = content.Length;
            while (i < n)
            {
                char c = content[i];
                if (c == '%') { while (i < n && content[i] != '\n' && content[i] != '\r') i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '(')
                {
                    string bruto = LerString(content, ref i);
                    int j = i;
                    while (j < n && char.IsWhiteSpace(content[j])) j++;
                    int j0 = j;
                    while (j < n && !char.IsWhiteSpace(content[j]) && content[j] != '(' && content[j] != '[') j++;
                    string op = content.Substring(j0, j - j0);
                    if (op == "Tj" || op == "'")
                    {
                        if (op == "'") y -= tl;
                        string txt = PdfKits.DecodePdfString(bruto);
                        if (txt.Trim().Length > 0)
                        {
                            var t = new TextoPos();
                            t.X = x; t.Y = y; t.Texto = txt;
                            res.Add(t);
                        }
                        i = j;
                    }
                    nums.Clear();
                    continue;
                }
                if (c == '-' || c == '.' || (c >= '0' && c <= '9'))
                {
                    int s0 = i; i++;
                    while (i < n && (content[i] == '.' || (content[i] >= '0' && content[i] <= '9'))) i++;
                    double v;
                    if (double.TryParse(content.Substring(s0, i - s0), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out v)) nums.Add(v);
                    continue;
                }
                if (c == '<')
                {
                    if (i + 1 < n && content[i + 1] == '<') i += 2;
                    else { while (i < n && content[i] != '>') i++; i++; }
                    nums.Clear();
                    continue;
                }
                if (c == '[')
                {
                    // TJ não aparece neste gerador; pula o array por segurança.
                    while (i < n && content[i] != ']')
                        if (content[i] == '(') LerString(content, ref i); else i++;
                    if (i < n) i++;
                    nums.Clear();
                    continue;
                }
                {
                    int s0 = i;
                    while (i < n && !char.IsWhiteSpace(content[i]) && content[i] != '(' &&
                           content[i] != '[' && content[i] != '<' && content[i] != '%') i++;
                    if (i == s0) { i++; continue; }
                    switch (content.Substring(s0, i - s0))
                    {
                        case "BT": x = 0; y = 0; break;
                        case "Tm": if (nums.Count >= 6) { x = nums[nums.Count - 2]; y = nums[nums.Count - 1]; } break;
                        case "Td": if (nums.Count >= 2) { x += nums[nums.Count - 2]; y += nums[nums.Count - 1]; } break;
                        case "TD": if (nums.Count >= 2) { tl = -nums[nums.Count - 1]; x += nums[nums.Count - 2]; y += nums[nums.Count - 1]; } break;
                        case "TL": if (nums.Count >= 1) tl = nums[nums.Count - 1]; break;
                        case "T*": y -= tl; break;
                    }
                    nums.Clear();
                    continue;
                }
            }
            return res;
        }

        // Lê "(...)" respeitando escapes e parênteses aninhados; devolve o miolo BRUTO
        // (os escapes são resolvidos depois por PdfKits.DecodePdfString).
        private static string LerString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;  // '('
            int depth = 1;
            while (i < s.Length)
            {
                char d = s[i];
                if (d == '\\' && i + 1 < s.Length) { sb.Append(d).Append(s[i + 1]); i += 2; continue; }
                if (d == '(') depth++;
                if (d == ')') { depth--; if (depth == 0) { i++; break; } }
                sb.Append(d); i++;
            }
            return sb.ToString();
        }

        // ---- Streams por página ----
        // O PdfKits.ConteudoInflado concatena TODOS os streams num texto só; aqui cada
        // página precisa ficar separada (linhas de páginas diferentes coincidem no y),
        // então a varredura stream/endstream é refeita devolvendo um item por stream.
        private static IEnumerable<string> Paginas(byte[] pdf)
        {
            byte[] sk = Encoding.ASCII.GetBytes("stream");
            byte[] ek = Encoding.ASCII.GetBytes("endstream");
            int i = 0;
            while (true)
            {
                int sIdx = IndexOf(pdf, sk, i);
                if (sIdx < 0) yield break;
                int dataStart = sIdx + sk.Length;
                if (dataStart < pdf.Length && pdf[dataStart] == 0x0D) dataStart++;
                if (dataStart < pdf.Length && pdf[dataStart] == 0x0A) dataStart++;
                int eIdx = IndexOf(pdf, ek, dataStart);
                if (eIdx < 0) yield break;
                string inflado = Inflar(pdf, dataStart, eIdx - dataStart);
                if (inflado != null) yield return inflado;
                i = eIdx + ek.Length;
            }
        }

        private static string Inflar(byte[] data, int off, int len)
        {
            if (len < 3) return null;
            // zlib = 2 bytes de cabeçalho + deflate cru (mesma tática do PdfKits).
            string r = TryInflate(data, off + 2, len - 2);
            if (r != null) return r;
            return TryInflate(data, off, len);
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
    }
}
