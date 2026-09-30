using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Listas de contagem da Farmácia Central (Estoque 5), por dia da semana (1..7) e
    // período (Diurno/Noturno). A estrutura é fixa (7 dias x 2 períodos = 14 listas),
    // mas os CÓDIGOS de cada lista são EDITÁVEIS pelo usuário e persistidos em
    // dados\contagem-farmacia.txt. A semente vem de ContagemListas (a escala original),
    // que continua sendo a referência de "Restaurar padrão".
    public static class ContagemFarmacia
    {
        public const int Estoque = 5;

        public static readonly string[] Periodos = { "Diurno", "Noturno" };

        private static readonly object _lock = new object();
        private static Dictionary<string, List<int>> _listas = new Dictionary<string, List<int>>();
        private static string _path;

        private static string Chave(int dia, string periodo) { return dia + "_" + periodo; }

        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                try
                {
                    string dirDados = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dirDados);
                    _path = Path.Combine(dirDados, "contagem-farmacia.txt");

                    if (File.Exists(_path))
                        _listas = Interpretar(File.ReadAllText(_path, Encoding.UTF8));
                    else
                        _listas = new Dictionary<string, List<int>>();
                }
                catch (Exception ex)
                {
                    Logger.Log("ContagemFarmacia: falha ao carregar (" + ex.Message + ") — usando a semente.");
                    _listas = new Dictionary<string, List<int>>();
                }
            }
        }

        // Códigos de uma lista: o que o usuário salvou, senão a semente (ContagemListas).
        public static List<int> Codigos(int dia, string periodo)
        {
            lock (_lock)
            {
                List<int> v;
                if (_listas.TryGetValue(Chave(dia, periodo), out v)) return new List<int>(v);
                return new List<int>(ContagemListas.Get(dia, periodo));
            }
        }

        // Códigos da SEMENTE (escala original) — base do "Restaurar padrão".
        public static List<int> Padrao(int dia, string periodo)
        {
            return new List<int>(ContagemListas.Get(dia, periodo));
        }

        // True se essa lista foi personalizada (difere da semente).
        public static bool Personalizada(int dia, string periodo)
        {
            lock (_lock) { return _listas.ContainsKey(Chave(dia, periodo)); }
        }

        // Substitui os códigos de uma lista e persiste (atômico, com rollback em falha).
        public static bool Salvar(int dia, string periodo, List<int> codigos)
        {
            lock (_lock)
            {
                string chave = Chave(dia, periodo);
                List<int> anterior; bool tinha = _listas.TryGetValue(chave, out anterior);
                _listas[chave] = new List<int>(codigos);
                if (Persistir()) return true;
                if (tinha) _listas[chave] = anterior; else _listas.Remove(chave);   // rollback
                return false;
            }
        }

        // Volta uma lista para a semente (remove a personalização) e persiste.
        public static bool RestaurarPadrao(int dia, string periodo)
        {
            lock (_lock)
            {
                string chave = Chave(dia, periodo);
                if (!_listas.ContainsKey(chave)) return true;   // já é a semente
                List<int> anterior = _listas[chave];
                _listas.Remove(chave);
                if (Persistir()) return true;
                _listas[chave] = anterior;   // rollback
                return false;
            }
        }

        // ===== interno =====

        // Formato: "[dia N Periodo]" abre uma lista; abaixo, um código por linha
        // (o que vem após " - " é só o nome do produto, ignorado).
        private static Dictionary<string, List<int>> Interpretar(string texto)
        {
            var mapa = new Dictionary<string, List<int>>();
            List<int> atual = null;
            foreach (var linha in texto.Split(new char[] { '\n' }))
            {
                string ln = linha.Trim().TrimEnd('\r');
                if (ln.Length == 0 || ln.StartsWith("#")) continue;

                if (ln.StartsWith("[dia", StringComparison.OrdinalIgnoreCase))
                {
                    atual = null;
                    int fecha = ln.IndexOf(']');
                    if (fecha < 0) continue;
                    // conteúdo entre "[dia" e "]": "N Periodo"
                    string corpo = ln.Substring(4, fecha - 4).Trim();
                    int esp = corpo.IndexOf(' ');
                    if (esp <= 0) continue;
                    int dia;
                    if (!int.TryParse(corpo.Substring(0, esp).Trim(), out dia)) continue;
                    if (dia < 1 || dia > 7) continue;
                    string periodo = corpo.Substring(esp + 1).Trim();
                    string periodoOk = PeriodoValido(periodo);
                    if (periodoOk == null) continue;
                    // Reaproveita a lista se a seção aparecer duplicada num arquivo
                    // editado à mão (mescla os códigos em vez de descartar a primeira).
                    string chave = Chave(dia, periodoOk);
                    if (!mapa.TryGetValue(chave, out atual)) { atual = new List<int>(); mapa[chave] = atual; }
                    continue;
                }

                if (atual == null) continue;
                int ign;
                foreach (int cd in ContagemCodigos.Parse(ln, out ign))
                    if (!atual.Contains(cd)) atual.Add(cd);
            }
            return mapa;
        }

        // Casa o período do arquivo (ignorando caixa) com o nome canônico.
        private static string PeriodoValido(string p)
        {
            foreach (var per in Periodos)
                if (string.Compare(per, p, StringComparison.OrdinalIgnoreCase) == 0) return per;
            return null;
        }

        private static bool Persistir()
        {
            if (_path == null) return false;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Listas de contagem da Farmácia Central (Estoque 5) — editadas pelo Auxiliar MV Soul.");
                sb.AppendLine("# Formato: \"[dia N Periodo]\" abre uma lista (N: 1=Segunda..7=Domingo); abaixo,");
                sb.AppendLine("# um código por linha. Só aparecem aqui as listas personalizadas.");
                for (int dia = 1; dia <= 7; dia++)
                {
                    foreach (var periodo in Periodos)
                    {
                        List<int> v;
                        if (!_listas.TryGetValue(Chave(dia, periodo), out v)) continue;
                        sb.AppendLine();
                        sb.AppendLine("[dia " + dia + " " + periodo + "]");
                        foreach (int cd in v)
                            sb.AppendLine(ContagemCodigos.LinhaComNome(cd));
                    }
                }
                return ContagemCodigos.GravarAtomico(_path, sb.ToString());
            }
            catch (Exception ex)
            {
                Logger.Log("ContagemFarmacia: falha ao gravar " + _path + ": " + ex.Message);
                return false;
            }
        }
    }
}
