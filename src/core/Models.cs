using System;

namespace SoulmvKit.Core
{
    // Um kit que pode ser produzido (lista LOV_FORMULA)
    public class KitOption
    {
        public int CdProduto { get; set; }     // código do kit (ex.: 13490)
        public string DsProduto { get; set; }   // descrição (ex.: KIT SONDA VESICAL DE DEMORA)
        public int CdFormula { get; set; }       // fórmula (ex.: 24)

        public override string ToString()
        {
            return CdProduto + " - " + DsProduto;
        }
    }

    // Um item da fórmula do kit (bloco ITFORMULA)
    public class ItemFormula
    {
        public int CdProduto { get; set; }
        public string DsProduto { get; set; }
        public string Unidade { get; set; }
        public decimal QtFormula { get; set; }   // quantidade necessária por kit
    }

    // Um lote disponível para um produto (lista LV_LOTE_PRODUZIDO)
    public class LoteOption
    {
        public string CdLote { get; set; }
        public DateTime? Validade { get; set; }
        public decimal SaldoAtual { get; set; }
    }

    // Resultado da escolha de lote
    public class ResultadoLote
    {
        public LoteOption Lote { get; set; }
        public bool SaldoSuficiente { get; set; }
        public string Aviso { get; set; }
    }

    // ZPL da etiqueta (gerado pelo servidor) + impressora de destino, para enviar ao LAS.
    public class EtiquetaZpl
    {
        public string Zpl { get; set; }
        public string PrinterId { get; set; }   // ex.: "LPT2"
        public int Copies { get; set; }
    }

    // Uma linha do preview do que seria produzido (produto + lote escolhido + qtd)
    public class PreviewItem
    {
        public ItemFormula Produto { get; set; }
        public LoteOption Lote { get; set; }
        public decimal Quantidade { get; set; }       // total nos N kits (QtFormula * numKits)
        public decimal QtdPorKit { get; set; }         // a receita (1 kit)
        public decimal Disponivel { get; set; }        // soma dos saldos dos lotes do item
        public bool Suficiente { get; set; }
        public string Aviso { get; set; }
    }

    // Um kit já produzido: o comprovante (PDF) + a etiqueta (ZPL) daquele kit.
    public class KitProduzido
    {
        public string ComprovanteUrl { get; set; }
        public EtiquetaZpl Etiqueta { get; set; }        // null se não foi gerada (não-fatal)
    }

    // Resultado de produzir VÁRIOS kits (N produções separadas, 1 código de barras cada).
    public class ProducaoVariosResult
    {
        public int Solicitados { get; set; }
        public System.Collections.Generic.List<KitProduzido> Kits = new System.Collections.Generic.List<KitProduzido>();
        public string Erro { get; set; }                // != null se parou antes de terminar
        public int Produzidos { get { return Kits.Count; } }
        public int ComEtiqueta
        {
            get { int n = 0; foreach (var k in Kits) if (k.Etiqueta != null) n++; return n; }
        }
    }

    // Uma unidade física de kit em estoque (consulta de kits): código de barras + código do kit.
    public class KitUnidade
    {
        public string CodBarras { get; set; }
        public string CodKit { get; set; }
    }

    // Um tipo de kit com as unidades em estoque (consulta de kits por produto).
    public class KitEstoque
    {
        public int CdKit { get; set; }
        public string Nome { get; set; }
        public int Formula { get; set; }
        public System.Collections.Generic.List<KitUnidade> Unidades = new System.Collections.Generic.List<KitUnidade>();
        public int Quantidade { get { return Unidades.Count; } }
    }

    // Resultado da consulta de kits: a lista de kits + a URL do PDF do relatório.
    public class ConsultaKitsResult
    {
        public string ReportUrl { get; set; }
        public System.Collections.Generic.List<KitEstoque> Kits = new System.Collections.Generic.List<KitEstoque>();
        public int TotalUnidades
        {
            get { int n = 0; foreach (var k in Kits) n += k.Quantidade; return n; }
        }
    }

    // Uma solicitação de saída para um estoque (form M_BAIXASOL, bloco SOLSAI_PRO).
    // Só leitura — para acompanhar o que foi pedido (não dá baixa).
    public class Solicitacao
    {
        public long Numero { get; set; }            // CD_SOLSAI_PRO
        public int Estoque { get; set; }            // CD_ESTOQUE (5 = Farmácia Central)
        public string Situacao { get; set; }        // TP_SITUACAO: P/S/A
        public string Origem { get; set; }          // TP_ORIGEM_SOLICITACAO: PRE/AVU/TRA
        public string Setor { get; set; }           // DSP_NM_SETOR
        public string Data { get; set; }            // DT_SOLSAI_PRO (dd/MM/yyyy)
        public string Hora { get; set; }            // HR_SOLSAI_PRO
        public bool Urgente { get; set; }           // SN_URGENTE = S
        public string Solicitante { get; set; }     // NM_USUARIO_SOLICITACAO
        public string Atendimento { get; set; }     // CD_ATENDIMENTO
        public string Paciente { get; set; }        // DSP_NM_PACIENTE (fallback NM_PACIENTE_INTEGRA)
        public string TpSol { get; set; }           // TP_SOLSAI_PRO: P=p/ paciente, S=pedido de setor, C=devolução

        // Origem "efetiva": TP_ORIGEM_SOLICITACAO quando preenchido; senão deduz do
        // TP_SOLSAI_PRO — pedidos de setor (S), devoluções (C) e pedidos de outro
        // estoque (E) vêm com a origem VAZIA (validado ao vivo: 445512/445552 = setor,
        // 445454/445568 = devolução, 445618 = estoque CAIXA ENDOSCOPIA).
        public string OrigemEfetiva
        {
            get
            {
                if (!string.IsNullOrEmpty(Origem)) return Origem;
                if (TpSol == "S") return "SET";     // pedido de setor (sem paciente)
                if (TpSol == "C") return "DEV";     // devolução de material/medicamento
                if (TpSol == "E") return "EST";     // pedido de outro estoque
                if (TpSol == "P" || string.IsNullOrEmpty(TpSol)) return "";
                return TpSol;                        // tipo desconhecido: mostra a letra (não some)
            }
        }

        // Rótulos amigáveis
        public string SituacaoLabel
        {
            get
            {
                switch (Situacao)
                {
                    case "P": return "Pendente";
                    case "S": return "Atendida";
                    case "A": return "Em atendimento";
                    default: return Situacao;
                }
            }
        }
        public string OrigemLabel
        {
            get
            {
                switch (OrigemEfetiva)
                {
                    case "PRE": return "Prescrição médica";
                    case "AVU": return "Material";
                    case "SET": return "Material";          // pedido de setor: a equipe trata como material
                    case "TRA": return "Transferência";
                    case "DEV": return "Devolução";
                    case "EST": return "Pedido de estoque"; // outro estoque pedindo à Farmácia Central
                    default: return OrigemEfetiva.Length == 0 ? "—" : OrigemEfetiva;
                }
            }
        }
    }

    // Resultado do rastreio de solicitações (com aviso se atingiu o teto de páginas).
    public class SolicitacoesResult
    {
        public System.Collections.Generic.List<Solicitacao> Itens = new System.Collections.Generic.List<Solicitacao>();
        public bool AtingiuTeto { get; set; }       // parou no limite de janelas (há mais além do coletado)
        public int TotalServidor { get; set; }      // totalRecords informado pelo servidor na 1ª página
    }
}
