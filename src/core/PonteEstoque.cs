using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Liga o antimicrobiano da PRESCRIÇÃO ao produto do ESTOQUE.
    //
    // O PEP não devolve o código de produto do ERP — só o nome do item prescrito. O nome
    // quase sempre casa com o do catálogo, mas não sempre: o hospital escreve
    // "MAXCEF (CEFEPIMA) 1G FA EV" na prescrição e "CEFEPIMA 1G PO INJ" no estoque, e a
    // teicoplanina aparece grafada "TEICLOPANINA" de um lado só.
    //
    // A ordem é: arquivo de-para (a farmácia manda) -> nome idêntico -> princípio ativo +
    // concentração, tolerando uma letra trocada e o nome comercial entre parênteses.
    // O que não casar fica SEM estoque na tela, com o motivo — nunca casa "no chute", que
    // aqui significaria mostrar o saldo de outro remédio.
    public static class PonteEstoque
    {
        private static string _caminho;
        // nome como aparece na prescrição (normalizado) -> código do produto no estoque
        private static readonly Dictionary<string, int> _dePara = new Dictionary<string, int>(StringComparer.Ordinal);

        public static void Init(string baseDir)
        {
            string dir = Path.Combine(baseDir, "dados");
            Directory.CreateDirectory(dir);
            _caminho = Path.Combine(dir, "antimicrobianos.txt");
            _dePara.Clear();
            try
            {
                if (!File.Exists(_caminho)) { CriarModelo(); return; }
                foreach (var linha in File.ReadAllLines(_caminho))
                {
                    string s = (linha ?? "").Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int cerq = s.IndexOf('#');
                    if (cerq >= 0) s = s.Substring(0, cerq).Trim();
                    int ig = s.IndexOf('=');
                    if (ig <= 0) continue;
                    int cod;
                    if (!int.TryParse(s.Substring(ig + 1).Trim(), out cod) || cod <= 0) continue;
                    _dePara[Normalizar(s.Substring(0, ig))] = cod;
                }
                Logger.Log("Ponte de estoque: " + _dePara.Count + " de-para (arquivo " + _caminho + ").");
            }
            catch (Exception ex) { Logger.Log("Ponte de estoque: falha ao ler " + _caminho + ": " + ex.Message); }
        }

        private static void CriarModelo()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# De-para entre o nome do antimicrobiano na PRESCRICAO e o codigo no ESTOQUE.");
                sb.AppendLine("#");
                sb.AppendLine("# O programa ja casa sozinho a maioria (nome igual, ou principio ativo +");
                sb.AppendLine("# concentracao). Este arquivo e para os que ele nao consegue: nome comercial");
                sb.AppendLine("# muito diferente, grafia trocada, apresentacao que nao bate.");
                sb.AppendLine("#");
                sb.AppendLine("# Quando um antimicrobiano aparecer na tela SEM estoque, copie o nome exato");
                sb.AppendLine("# que aparece na coluna \"Antimicrobiano\" e escreva aqui com o codigo do produto:");
                sb.AppendLine("#");
                sb.AppendLine("#   MAXCEF (CEFEPIMA) 1G FA EV=9012345");
                sb.AppendLine("#");
                sb.AppendLine("# Depois de editar, feche e abra o programa.");
                sb.AppendLine();
                File.WriteAllText(_caminho, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Logger.Log("Ponte de estoque: não consegui criar " + _caminho + ": " + ex.Message); }
        }

        // Catálogo de uma varredura do R_MOVI_ESTOQ, pronto para consulta.
        public class Catalogo
        {
            internal readonly Dictionary<string, SaldoProduto> PorNome = new Dictionary<string, SaldoProduto>(StringComparer.Ordinal);
            internal readonly Dictionary<int, SaldoProduto> PorCodigo = new Dictionary<int, SaldoProduto>();
            internal readonly List<SaldoProduto> Todos = new List<SaldoProduto>();

            public Catalogo(IEnumerable<SaldoProduto> produtos)
            {
                foreach (var p in produtos)
                {
                    if (p == null || p.Nome == null) continue;
                    Todos.Add(p);
                    PorNome[Normalizar(p.Nome)] = p;
                    PorCodigo[p.Codigo] = p;
                }
            }
            public int Count { get { return Todos.Count; } }
        }

        // Acha o produto de estoque correspondente. null = não deu para ligar com segurança.
        public static SaldoProduto Resolver(Catalogo cat, string nomeDaPrescricao)
        {
            return Resolver(cat, nomeDaPrescricao, null, true);
        }

        // unidadePrescricao: o productUnit do item ("FA C/500UI/MIL") — quando declara a
        // embalagem, é MAIS confiável que o nome (a polimixina vem grafada "500.00UI" na
        // prescrição e "500.000 UI" no catálogo; a unidade é idêntica nos dois lados).
        //
        // catalogoCompleto: true quando o catálogo cobre a espécie INTEIRA — só então a
        // ausência de um produto do de-para pode ser lida como "saldo zerado". Num
        // catálogo parcial (uma classe só), ausência vira null e o chamador tenta de novo
        // com o catálogo largo.
        public static SaldoProduto Resolver(Catalogo cat, string nomeDaPrescricao,
            string unidadePrescricao, bool catalogoCompleto)
        {
            if (cat == null || string.IsNullOrEmpty(nomeDaPrescricao)) return null;
            string n = Normalizar(nomeDaPrescricao);

            // 1) o que a farmácia mandou vale acima de tudo
            int cod;
            if (_dePara.TryGetValue(n, out cod))
            {
                SaldoProduto fixo;
                if (cat.PorCodigo.TryGetValue(cod, out fixo)) return fixo;
                // Está no de-para mas não veio no relatório. Ele traz TUDO que tem saldo
                // diferente de zero, então num catálogo completo ausência = zerado.
                if (catalogoCompleto)
                    return new SaldoProduto { Codigo = cod, Nome = nomeDaPrescricao, Saldo = 0 };
                return null;
            }

            // 2) nome idêntico
            SaldoProduto p;
            if (cat.PorNome.TryGetValue(n, out p)) return p;

            // O princípio ativo pode estar entre parênteses ("MAXCEF (CEFEPIMA)"),
            // então tenta as duas leituras.
            var termos = new List<string>();
            string primeiro = PrimeiraPalavra(n);
            if (primeiro.Length >= 4) termos.Add(primeiro);
            var par = Regex.Match(n, @"\(([A-Z][A-Z ]{3,})\)");
            if (par.Success) termos.Add(PrimeiraPalavra(par.Groups[1].Value.Trim()));

            // 3) princípio ativo + MESMA DOSAGEM por embalagem ("C/500UI/MIL" = 500 mil
            // UI, "C/500MG"). O token vem preferencialmente da unidade prescrita; sem
            // ela, do nome. Só dosagem conta — volume ("C/2ML") não identifica a
            // apresentação e fica de fora (ver ConversaoDose.TokenDosagem).
            string token = ConversaoDose.TokenDosagem(Normalizar(unidadePrescricao));
            if (token == null) token = ConversaoDose.TokenDosagem(n);
            if (token != null)
            {
                var comToken = new List<SaldoProduto>();
                foreach (var c in cat.Todos)
                {
                    string cn = Normalizar(c.Nome);
                    if (!MesmaFamilia(termos, cn)) continue;
                    if (token == ConversaoDose.TokenDosagem(cn)) comToken.Add(c);
                }
                if (comToken.Count == 1) return comToken[0];
            }

            // 4) princípio ativo + concentração.
            string conc = Concentracao(n);
            var achados = new List<SaldoProduto>();
            foreach (var c in cat.Todos)
            {
                string cn = Normalizar(c.Nome);
                if (!MesmaFamilia(termos, cn)) continue;
                string cc = Concentracao(cn);
                // Concentração diferente = apresentação diferente: não pode casar, senão a
                // tela mostraria o saldo da ampola de 500mg para quem usa a de 1g.
                if (conc.Length > 0 && cc.Length > 0 && conc != cc) continue;
                achados.Add(c);
            }
            return achados.Count == 1 ? achados[0] : null;   // ambíguo também é "não sei"
        }

        // Grafias que o hospital usa para o MESMO princípio ativo: a prescrição diz
        // "PENICILINA BENZATINA", o catálogo diz "BENZILPENICILINA" — penicilina G e
        // benzilpenicilina são o mesmo fármaco (caso real de 29/08, produto 13583).
        // Lista FECHADA e explícita: nada de deduzir por prefixo, senão qualquer
        // "...PENICILINA" casaria com qualquer outra. A concentração continua mandando
        // (apresentação diferente não liga) e dois candidatos seguem ambíguos (não liga).
        private static readonly string[][] _sinonimos =
        {
            new[] { "PENICILINA", "BENZILPENICILINA" },
        };

        private static bool Sinonimos(string a, string b)
        {
            foreach (var grupo in _sinonimos)
            {
                bool temA = false, temB = false;
                foreach (var s in grupo) { if (s == a) temA = true; if (s == b) temB = true; }
                if (temA && temB) return true;
            }
            return false;
        }

        // Primeira palavra do catálogo bate com algum dos termos da prescrição?
        private static bool MesmaFamilia(List<string> termos, string nomeCatalogo)
        {
            string cp = PrimeiraPalavra(nomeCatalogo);
            if (cp.Length < 4) return false;
            foreach (string t in termos)
                if (t == cp || Perto(t, cp) || Sinonimos(t, cp)) return true;
            return false;
        }

        // Uma letra trocada basta para o nome não casar. Duas regras, ambas conservadoras:
        //
        //  - distância 1 em palavra longa (uma letra a mais, a menos ou trocada);
        //  - MESMAS LETRAS em ordem diferente ("TEICLOPANINA" x "TEICOPLANINA", que é como
        //    a teicoplanina aparece grafada de um lado só).
        //
        // Não subo para distância 2: AMOXICILINA e AMPICILINA distam 2 e são remédios
        // diferentes. Anagrama é seguro justamente porque duas drogas distintas não têm
        // exatamente as mesmas letras.
        private static bool Perto(string a, string b)
        {
            if (a == b) return true;
            if (a.Length < 7 || b.Length < 7) return false;
            if (a.Length == b.Length && Assinatura(a) == Assinatura(b)) return true;
            if (Math.Abs(a.Length - b.Length) > 1) return false;
            return Distancia(a, b) <= 1;
        }

        private static string Assinatura(string s)
        {
            var c = s.ToCharArray();
            Array.Sort(c);
            return new string(c);
        }

        private static int Distancia(string a, string b)
        {
            var linha = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) linha[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int ant = linha[0]; linha[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int tmp = linha[j];
                    int custo = a[i - 1] == b[j - 1] ? 0 : 1;
                    linha[j] = Math.Min(Math.Min(linha[j] + 1, linha[j - 1] + 1), ant + custo);
                    ant = tmp;
                }
                if (i > 2 && linha[b.Length] > 2) { /* segue: a linha ainda pode cair */ }
            }
            return linha[b.Length];
        }

        private static string PrimeiraPalavra(string s)
        { var m = Regex.Match(s, @"^[A-Z]+"); return m.Success ? m.Value : ""; }

        // "500MG" -> "500MG"; "1.200.000 UI" -> "1200000UI" (o PONTO DE MILHAR cai);
        // "4,5G" -> "4.5G" (vírgula decimal vira ponto). Usada para não confundir
        // apresentações do mesmo princípio. Sem canonizar o milhar, a benzilpenicilina
        // "1.200.000 UI" do catálogo era lida como "200.000" (o regex antigo tropeçava
        // no primeiro ponto) e nunca casava com o "1200000UI" da prescrição.
        private static string Concentracao(string s)
        {
            var m = Regex.Match(s, @"(\d[\d.,]*?)\s*(MG|MCG|UI|G)\b");
            if (!m.Success) return "";
            string num = m.Groups[1].Value.TrimEnd('.', ',');
            if (Regex.IsMatch(num, @"^\d{1,3}(\.\d{3})+$")) num = num.Replace(".", "");
            return num.Replace(",", ".") + m.Groups[2].Value;
        }

        private static string Normalizar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return Regex.Replace(sb.ToString().ToUpperInvariant(), @"\s+", " ").Trim();
        }
    }
}
