using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Uma linha do pré-pedido no momento do envio: o que a aba sugeriu e o que foi pedido.
    public class HistoricoLinha
    {
        public int Codigo;
        public string Nome;
        public string Unidade;
        public decimal Consumo;       // "Saiu" no período do pedido (2 ou 3 dias)
        public decimal Estoque;       // saldo na Farmácia Central
        public bool TemCaf;
        public decimal EstoqueCaf;    // saldo na CAF (só vale com TemCaf)
        public int Caixa;             // embalagem cadastrada (0 = sem cadastro)
        public int Sugestao;
        public int Pedido;            // quantidade enviada (0 = ficou fora)
        public bool Recusado;         // o MV recusou o produto: pedido mas não entrou
    }

    // Registro local de SUGESTÃO × ENVIADO por produto (dados\pedido-historico.csv), uma
    // linha por produto do pré-pedido a cada pedido criado no MV.
    //
    // Por que existe: é a única fonte para calibrar a regra da sugestão. O servidor de
    // relatórios do MV apaga os PDFs em cerca de um dia (em 26/11/2026 só o pedido da
    // véspera pôde ser comparado) e o log guarda só os totais. Entram TODAS as linhas, também
    // as que ficaram fora: "sugeriu e o farmacêutico tirou" e "não sugeriu e ele incluiu"
    // são justamente os casos que dizem onde a regra erra.
    //
    // Formato para abrir no Excel pt-BR: separador ';', vírgula decimal, UTF-8 com BOM.
    // Não guarda quem enviou — o arquivo é sobre a regra, não sobre a pessoa.
    public static class PedidoHistorico
    {
        private static readonly object _lock = new object();
        private static string _dir;

        private const string Arquivo = "pedido-historico.csv";
        // Se o arquivo principal estiver aberto no Excel a gravação falha; as linhas vão
        // para este, para o pedido não ficar sem registro.
        private const string ArquivoReserva = "pedido-historico-pendente.csv";

        private const string Cabecalho =
            "enviado_em;numero;tipo;periodo_dias;janela_dias;codigo;produto;unidade;saiu;estoque;" +
            "estoque_caf;caixa;cobertura_dias;sugestao;enviado;recusado_mv";

        private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

        public static void Init(string baseDir)
        {
            lock (_lock)
            {
                try
                {
                    _dir = Path.Combine(baseDir, "dados");
                    Directory.CreateDirectory(_dir);
                }
                catch (Exception ex) { _dir = null; Logger.Log("Histórico do pedido: pasta indisponível: " + ex.Message); }
            }
        }

        // NUNCA lança: é chamado logo depois de o pedido ser criado no MV, e uma exceção ali
        // seria lida pela tela como falha de envio. Devolve false se nada foi gravado.
        public static bool Registrar(DateTime quando, long numero, string tipo, int periodoDias,
            double janelaDias, List<HistoricoLinha> linhas)
        {
            try
            {
                if (linhas == null || linhas.Count == 0) return false;
                string texto = Montar(quando, numero, tipo, periodoDias, janelaDias, linhas);
                lock (_lock)
                {
                    if (_dir == null) return false;
                    string principal = Path.Combine(_dir, Arquivo);
                    // Arquivo com OUTRO cabeçalho (formato de uma versão de teste): não mistura
                    // colunas — o antigo é guardado com a data no nome e começa um novo.
                    GuardarSeFormatoDiferente(principal);
                    if (Acrescentar(principal, texto)) return true;
                    bool ok = Acrescentar(Path.Combine(_dir, ArquivoReserva), texto);
                    Logger.Log("Histórico do pedido: " + Arquivo + " indisponível (aberto em outro programa?) — " +
                        (ok ? "gravado em " + ArquivoReserva + "." : "pedido " + numero + " ficou SEM registro."));
                    return ok;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Histórico do pedido: falha ao registrar o pedido " + numero + ": " + ex.Message);
                return false;
            }
        }

        // Texto das linhas (sem o cabeçalho) — separado para o teste conferir o formato.
        public static string Montar(DateTime quando, long numero, string tipo, int periodoDias,
            double janelaDias, List<HistoricoLinha> linhas)
        {
            var sb = new StringBuilder();
            string inicio = quando.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + ";" +
                numero.ToString(CultureInfo.InvariantCulture) + ";" + Texto(tipo) + ";" +
                periodoDias.ToString(CultureInfo.InvariantCulture) + ";" + janelaDias.ToString("0.00", Pt) + ";";
            foreach (var l in linhas)
            {
                if (l == null) continue;
                double cob = PedidoSugestao.Cobertura(l.Consumo, l.Estoque, janelaDias);
                sb.Append(inicio)
                  .Append(l.Codigo.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(Texto(l.Nome)).Append(';')
                  .Append(Texto(l.Unidade)).Append(';')
                  .Append(Numero(l.Consumo)).Append(';')
                  .Append(Numero(l.Estoque)).Append(';')
                  .Append(l.TemCaf ? Numero(l.EstoqueCaf) : "").Append(';')
                  .Append(l.Caixa > 1 ? l.Caixa.ToString(CultureInfo.InvariantCulture) : "").Append(';')
                  // sem consumo não há cobertura a calcular: fica vazio (não "infinito")
                  .Append(double.IsInfinity(cob) ? "" : cob.ToString("0.0", Pt)).Append(';')
                  .Append(l.Sugestao.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(l.Pedido.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(l.Recusado ? "S" : "")
                  .Append("\r\n");
            }
            return sb.ToString();
        }

        private static void GuardarSeFormatoDiferente(string caminho)
        {
            try
            {
                if (!File.Exists(caminho) || new FileInfo(caminho).Length == 0) return;
                string primeira;
                using (var r = new StreamReader(caminho, Encoding.UTF8)) primeira = r.ReadLine();
                if (primeira == null || primeira.Trim() == Cabecalho) return;
                string guardado = Path.Combine(Path.GetDirectoryName(caminho),
                    "pedido-historico-ate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
                File.Move(caminho, guardado);
                Logger.Log("Histórico do pedido: formato anterior guardado em " + Path.GetFileName(guardado) + ".");
            }
            catch (Exception ex) { Logger.Log("Histórico do pedido: não foi possível guardar o formato anterior: " + ex.Message); }
        }

        private static bool Acrescentar(string caminho, string texto)
        {
            try
            {
                bool novo = !File.Exists(caminho) || new FileInfo(caminho).Length == 0;
                using (var fs = new FileStream(caminho, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var w = new StreamWriter(fs, new UTF8Encoding(novo)))   // BOM só no arquivo novo
                {
                    if (novo) w.Write(Cabecalho + "\r\n");
                    w.Write(texto);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static string Numero(decimal v)
        {
            return v == decimal.Truncate(v)
                ? decimal.Truncate(v).ToString("0", CultureInfo.InvariantCulture)
                : v.ToString("0.####", Pt);
        }

        // Campo de texto: sem ';' nem quebra de linha (quebrariam a coluna) e sem começar
        // com = + - @ (o Excel leria como fórmula — o nome vem do cadastro do MV).
        private static string Texto(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'').Trim();
            if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
            return s;
        }
    }
}
