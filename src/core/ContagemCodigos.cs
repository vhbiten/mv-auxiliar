using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Utilidades compartilhadas pelas listas de contagem (Farmácia Central e CAF):
    // interpretação de códigos digitados/colados e gravação atômica em disco.
    public static class ContagemCodigos
    {
        // Número no começo do texto (até 9 dígitos); 0 se não começa com dígito ou
        // se o número não é seguido de separador ("12x34", "9%..." não são código —
        // evita fragmentos de nome colado virarem código fantasma). Permite "16576-".
        public static int CodigoInicial(string s)
        {
            if (s == null) return 0;
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == 0 || i > 9) return 0;
            if (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '-') return 0;
            int v; return int.TryParse(s.Substring(0, i), out v) ? v : 0;
        }

        // Extrai os códigos de um texto livre: um por linha ou separados por vírgula/;,
        // aceitando "13504 - ABOCATH 14G" (só o número inicial conta). Descarta o nome
        // (" - DESCRIÇÃO") ANTES de dividir por vírgula, pois nomes têm vírgula decimal.
        // Linhas com conteúdo mas sem código são contadas em 'ignoradas'. Sem duplicatas.
        public static List<int> Parse(string texto, out int ignoradas)
        {
            ignoradas = 0;
            var lista = new List<int>();
            var vistos = new HashSet<int>();
            if (texto == null) return lista;
            foreach (var linha in texto.Split(new char[] { '\n' }))
            {
                string ln = linha.Trim().TrimEnd('\r');
                if (ln.Length == 0 || ln.StartsWith("#")) continue;
                int corte = ln.IndexOf(" - ", StringComparison.Ordinal);
                if (corte >= 0) ln = ln.Substring(0, corte);
                foreach (var parte in ln.Split(new char[] { ',', ';' }))
                {
                    string p = parte.Trim();
                    if (p.Length == 0) continue;
                    int cd = CodigoInicial(p);
                    if (cd <= 0) { ignoradas++; continue; }
                    if (vistos.Add(cd)) lista.Add(cd);
                }
            }
            return lista;
        }

        // Gravação atômica: escreve num temporário e troca de uma vez, para que um
        // kill/queda no meio não deixe o arquivo final truncado. Loga e devolve false em falha.
        public static bool GravarAtomico(string path, string conteudo)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, conteudo, Encoding.UTF8);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("Gravação atômica falhou em " + path + ": " + ex.Message);
                return false;
            }
        }

        // Linha de arquivo para um código, com o nome do produto quando conhecido.
        public static string LinhaComNome(int cd)
        {
            string nome = ProdutoNomes.Get(cd);
            return nome != null ? cd + " - " + nome : cd.ToString();
        }
    }
}
