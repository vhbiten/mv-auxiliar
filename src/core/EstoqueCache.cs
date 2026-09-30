using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Guarda a última análise de estoque da CAF em dados\estoque-cache.txt: gerar custa
    // ~30s de conversa com o MV, então ao reabrir o app a tela mostra a análise anterior
    // na hora (com a data visível) e o farmacêutico decide se atualiza.
    //
    // Formato (mesmo padrão do SolicitacoesCache): campos separados por U+001F; primeira
    // linha é o cabeçalho (versão, quando, dias, cadastroMvOk, os setores e, no fim,
    // especiesOk), depois uma linha por produto. Números em InvariantCulture. Carregar é
    // tolerante: qualquer linha estranha é pulada; arquivo de versão desconhecida é
    // ignorado inteiro.
    public static class EstoqueCache
    {
        private const char Sep = '';   // US (unit separator): nunca aparece em nome de produto
        private static readonly object _lock = new object();
        private static string _path;

        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                try
                {
                    string dir = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, "estoque-cache.txt");
                }
                catch (Exception ex) { Logger.Log("EstoqueCache: init falhou: " + ex.Message); }
            }
        }

        public static void Salvar(AnaliseResultado r)
        {
            if (_path == null || r == null) return;
            lock (_lock)
            {
                try
                {
                    var inv = CultureInfo.InvariantCulture;
                    var sb = new StringBuilder();
                    sb.Append("v1").Append(Sep)
                      .Append(r.Quando.ToString("o", inv)).Append(Sep)
                      .Append(r.DiasPeriodo).Append(Sep)
                      .Append(r.CadastroMvOk ? "S" : "N").Append(Sep)
                      .Append(r.SetorCodigos.Length);
                    for (int i = 0; i < r.SetorCodigos.Length; i++)
                        sb.Append(Sep).Append(r.SetorCodigos[i])
                          .Append(Sep).Append(C(r.SetorNomes[i]))
                          .Append(Sep).Append(r.SetorOk != null && i < r.SetorOk.Length && r.SetorOk[i] ? "S" : "N");
                    // Campo novo no FIM do cabeçalho (04/09/2026): fica depois dos setores
                    // para o leitor antigo, que conta campos a partir de nSet, não tropeçar.
                    sb.Append(Sep).Append(r.EspeciesOk ? "S" : "N");
                    sb.AppendLine();

                    foreach (var p in r.Itens)
                    {
                        sb.Append(p.Codigo).Append(Sep).Append(C(p.Nome)).Append(Sep).Append(C(p.Unidade))
                          .Append(Sep).Append(p.Especie)
                          .Append(Sep).Append(p.Consumo.ToString(inv))
                          .Append(Sep).Append(p.SaldoCaf.ToString(inv))
                          .Append(Sep).Append(p.VlUnitario.ToString(inv))
                          .Append(Sep).Append(p.EstoqueMinimo.ToString(inv))
                          .Append(Sep).Append(p.EstoqueMaximo.ToString(inv))
                          .Append(Sep).Append(p.PontoPedido.ToString(inv))
                          .Append(Sep).Append(C(p.Fornecedor))
                          .Append(Sep).Append(p.UltimaEntrada.HasValue ? p.UltimaEntrada.Value.ToString("o", inv) : "");
                        for (int i = 0; i < p.SaldoSetores.Length; i++)
                            sb.Append(Sep).Append(p.SaldoSetores[i].ToString(inv));
                        // Campo novo no FIM da linha (cache antigo sem ele continua legível).
                        sb.Append(Sep).Append(p.Consumo4m.ToString(inv));
                        sb.AppendLine();
                    }
                    File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
                }
                catch (Exception ex) { Logger.Log("EstoqueCache: salvar falhou: " + ex.Message); }
            }
        }

        // null = sem cache utilizável.
        public static AnaliseResultado Carregar()
        {
            if (_path == null) return null;
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path)) return null;
                    var linhas = File.ReadAllLines(_path, Encoding.UTF8);
                    if (linhas.Length < 1) return null;
                    var h = linhas[0].Split(Sep);
                    if (h.Length < 5 || h[0] != "v1") return null;

                    var inv = CultureInfo.InvariantCulture;
                    var r = new AnaliseResultado();
                    DateTime quando;
                    if (!DateTime.TryParse(h[1], inv, DateTimeStyles.RoundtripKind, out quando)) return null;
                    r.Quando = quando;
                    int dias; if (!int.TryParse(h[2], out dias) || dias <= 0) return null;
                    r.DiasPeriodo = dias;
                    r.CadastroMvOk = h[3] == "S";
                    int nSet; if (!int.TryParse(h[4], out nSet) || nSet < 0 || nSet > 20) return null;
                    if (h.Length < 5 + nSet * 3) return null;
                    r.SetorCodigos = new int[nSet];
                    r.SetorNomes = new string[nSet];
                    r.SetorOk = new bool[nSet];
                    for (int i = 0; i < nSet; i++)
                    {
                        if (!int.TryParse(h[5 + i * 3], out r.SetorCodigos[i])) return null;
                        r.SetorNomes[i] = h[6 + i * 3];
                        r.SetorOk[i] = h[7 + i * 3] == "S";
                    }
                    // Cache de versão anterior não tem o campo — e nele os produtos fora do
                    // R_PROD_CRIT estão mesmo sem espécie, então false é a verdade (o
                    // banner manda gerar de novo).
                    int iEsp = 5 + nSet * 3;
                    r.EspeciesOk = h.Length > iEsp && h[iEsp] == "S";

                    for (int li = 1; li < linhas.Length; li++)
                    {
                        var f = linhas[li].Split(Sep);
                        if (f.Length < 12 + nSet) continue;
                        var p = new ProdutoEstoque();
                        if (!int.TryParse(f[0], out p.Codigo) || p.Codigo <= 0) continue;
                        p.Nome = f[1]; p.Unidade = f[2];
                        int esp; int.TryParse(f[3], out esp); p.Especie = esp;
                        if (!decimal.TryParse(f[4], NumberStyles.Any, inv, out p.Consumo)) continue;
                        if (!decimal.TryParse(f[5], NumberStyles.Any, inv, out p.SaldoCaf)) continue;
                        decimal.TryParse(f[6], NumberStyles.Any, inv, out p.VlUnitario);
                        decimal.TryParse(f[7], NumberStyles.Any, inv, out p.EstoqueMinimo);
                        decimal.TryParse(f[8], NumberStyles.Any, inv, out p.EstoqueMaximo);
                        decimal.TryParse(f[9], NumberStyles.Any, inv, out p.PontoPedido);
                        p.Fornecedor = f[10];
                        DateTime ue;
                        if (f[11].Length > 0 && DateTime.TryParse(f[11], inv, DateTimeStyles.RoundtripKind, out ue))
                            p.UltimaEntrada = ue;
                        p.SaldoSetores = new decimal[nSet];
                        for (int i = 0; i < nSet; i++)
                            decimal.TryParse(f[12 + i], NumberStyles.Any, inv, out p.SaldoSetores[i]);
                        if (f.Length > 12 + nSet)   // opcional: cache antigo não tem
                            decimal.TryParse(f[12 + nSet], NumberStyles.Any, inv, out p.Consumo4m);
                        r.Itens.Add(p);
                    }
                    return r.Itens.Count > 0 ? r : null;
                }
                catch (Exception ex)
                {
                    Logger.Log("EstoqueCache: carregar falhou: " + ex.Message);
                    return null;
                }
            }
        }

        private static string C(string s)
        {
            if (s == null) return "";
            return s.Replace(Sep, ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
