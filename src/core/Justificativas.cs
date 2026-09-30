using System;
using System.Collections.Generic;

namespace SoulmvKit.Core
{
    // Uma justificativa de uso de antimicrobiano emitida no PEP.
    //
    // No MV isso é um DOCUMENTO CLÍNICO do template 501 ("JUSTIFICATIVA ANTIMICROBIANO").
    // O conteúdo preenchido (medicamento, posologia, o texto da justificativa) mora no
    // editor Flash e não sai pela API REST; o que sai — e é o que a farmácia precisa para
    // controlar — é a EMISSÃO: quem, para quem, quando, em que setor e se foi assinada.
    public class Justificativa
    {
        public int Id;                 // id do documento clínico
        public DateTime Data;          // documentDate (local)
        public DateTime Fechado;       // quando foi concluída (pode vir zerada)
        public string Situacao;        // SIGNED / CLOSED / OPENED / CANCELED
        public int PacienteId;
        public string Paciente;
        public int Atendimento;        // hospitalAdmission — a mesma chave da aba Antimicrobianos
        public string Setor;           // sectorName (vazio quando não há internação)
        public string TipoAtendimento; // INPATIENT / EMERGENCY / ROUTINE
        public string Leito;
        public string Medico;          // creationProvider
        public string Conselho;        // "CRM 11860"
        public string Cid;             // "J22 — INFECÇÕES AGUDAS..."

        // Onde o paciente estava. Quase um quarto das justificativas vem sem setor: são
        // atendimentos sem internação (pronto-socorro, ambulatório). Deixar a célula vazia
        // pareceria dado faltando, quando na verdade setor ali não existe.
        public string Local
        {
            get
            {
                if (!string.IsNullOrEmpty(Setor)) return Setor;
                switch ((TipoAtendimento ?? "").ToUpperInvariant())
                {
                    case "EMERGENCY": return "Pronto-socorro";
                    case "ROUTINE": return "Ambulatório";
                    case "DIAGNOSIS": return "Diagnóstico";
                    default: return "—";
                }
            }
        }

        // Situação em português, para a tela não mostrar o enum cru do servidor.
        public string SituacaoTexto
        {
            get
            {
                switch ((Situacao ?? "").ToUpperInvariant())
                {
                    case "SIGNED": return "Assinada";
                    case "CLOSED": return "Sem assinatura";
                    case "OPENED": return "Rascunho";
                    case "CANCELED": case "CANCELLED": return "Cancelada";
                    default: return string.IsNullOrEmpty(Situacao) ? "—" : Situacao;
                }
            }
        }

        // O MV só emite o PDF do documento ASSINADO. "Concluída" é o médico ter fechado
        // o texto sem assinar ainda — vale como justificativa para o cruzamento, mas o
        // servlet de impressão devolve zero byte. Verificado item a item.
        public bool Assinada
        {
            get { return (Situacao ?? "").ToUpperInvariant() == "SIGNED"; }
        }

        // Só a assinada vale como documento pronto; "em aberto" é rascunho do médico.
        public bool Pendente
        {
            get
            {
                string s = (Situacao ?? "").ToUpperInvariant();
                return s == "OPENED";
            }
        }
    }

    public class VarreduraJustificativas
    {
        public readonly List<Justificativa> Itens = new List<Justificativa>();
        public DateTime Desde, Ate;
        public bool AtingiuTeto;    // parou no limite de páginas: há mais no período
        public string Aviso;
    }
}
