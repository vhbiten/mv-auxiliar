using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Cache em disco do último resultado de solicitações (por combinação de filtros), em
    // dados\solicitacoes-cache.txt. Permite reabrir a aba instantaneamente e sobrevive a
    // reinício do app. Formato: 1ª linha = cabeçalho (filtro, quando, maiorNúmero);
    // demais linhas = 1 solicitação, campos separados por US (, não aparece nos dados).
    public static class SolicitacoesCache
    {
        private static readonly object _lock = new object();
        private static string _path;
        private const char US = '\u001F';

        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                try
                {
                    string dir = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, "solicitacoes-cache.txt");
                }
                catch (Exception ex) { Logger.Log("SolicitacoesCache init: " + ex.Message); }
            }
        }

        public class Snapshot
        {
            public string FiltroKey;
            public DateTime Quando;
            public long MaxNumero;
            public List<Solicitacao> Itens = new List<Solicitacao>();
        }

        public static void Salvar(string filtroKey, List<Solicitacao> itens, long maxNumero, DateTime quando)
        {
            lock (_lock)
            {
                if (_path == null || itens == null) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.Append(filtroKey).Append(US).Append(quando.ToString("o")).Append(US).Append(maxNumero).Append('\n');
                    foreach (var s in itens)
                    {
                        sb.Append(s.Numero).Append(US).Append(s.Estoque).Append(US).Append(C(s.Situacao)).Append(US)
                          .Append(C(s.Origem)).Append(US).Append(C(s.Setor)).Append(US).Append(C(s.Data)).Append(US)
                          .Append(C(s.Hora)).Append(US).Append(s.Urgente ? "1" : "0").Append(US).Append(C(s.Solicitante)).Append(US)
                          .Append(C(s.Atendimento)).Append(US).Append(C(s.Paciente)).Append(US).Append(C(s.TpSol)).Append('\n');
                    }
                    File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
                }
                catch (Exception ex) { Logger.Log("SolicitacoesCache salvar: " + ex.Message); }
            }
        }

        public static Snapshot Carregar()
        {
            lock (_lock)
            {
                if (_path == null || !File.Exists(_path)) return null;
                try
                {
                    var linhas = File.ReadAllLines(_path, Encoding.UTF8);
                    if (linhas.Length == 0) return null;
                    var cab = linhas[0].Split(US);
                    if (cab.Length < 3) return null;
                    var snap = new Snapshot();
                    snap.FiltroKey = cab[0];
                    DateTime.TryParse(cab[1], null, DateTimeStyles.RoundtripKind, out snap.Quando);
                    long.TryParse(cab[2], out snap.MaxNumero);
                    for (int i = 1; i < linhas.Length; i++)
                    {
                        var f = linhas[i].Split(US);
                        if (f.Length < 11) continue;
                        var s = new Solicitacao();
                        long n; long.TryParse(f[0], out n); s.Numero = n;
                        int est; int.TryParse(f[1], out est); s.Estoque = est;
                        s.Situacao = f[2]; s.Origem = f[3]; s.Setor = f[4]; s.Data = f[5]; s.Hora = f[6];
                        s.Urgente = f[7] == "1"; s.Solicitante = f[8]; s.Atendimento = f[9]; s.Paciente = f[10];
                        if (f.Length >= 12) s.TpSol = f[11];   // coluna nova; cache antigo não a tem
                        snap.Itens.Add(s);
                    }
                    return snap;
                }
                catch (Exception ex) { Logger.Log("SolicitacoesCache carregar: " + ex.Message); return null; }
            }
        }

        // remove separadores/quebras de linha de um campo (não devem aparecer, mas por segurança)
        private static string C(string v)
        {
            if (v == null) return "";
            return v.Replace(US, ' ').Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}
