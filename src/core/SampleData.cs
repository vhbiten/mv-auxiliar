using System.Collections.Generic;

namespace SoulmvKit.Core
{
    // DADOS DE EXEMPLO (temporário) - os 14 kits capturados do sistema real.
    // Será substituído pela leitura ao vivo (LIST_VALUES CD_KIT) quando o
    // MorphisClient estiver conectado na LAN.
    public static class SampleData
    {
        public static List<KitOption> Kits()
        {
            return new List<KitOption>
            {
                new KitOption { CdProduto = 13488, DsProduto = "KIT ACESSO VENOSO CENTRAL DUPLO LUMEN", CdFormula = 1 },
                new KitOption { CdProduto = 13489, DsProduto = "KIT ACESSO VENOSO CENTRAL - HEMODIALISE", CdFormula = 2 },
                new KitOption { CdProduto = 13490, DsProduto = "KIT SONDA VESICAL DE DEMORA", CdFormula = 24 },
                new KitOption { CdProduto = 14178, DsProduto = "KIT RADIO - INTERVENCAO", CdFormula = 13 },
                new KitOption { CdProduto = 13455, DsProduto = "KIT ACESSO VENOSO CENTRAL TRIPLO LUMEN", CdFormula = 9 },
                new KitOption { CdProduto = 14145, DsProduto = "KIT CATETER ACESSO VENOSO CENTRAL MONO LUMEN", CdFormula = 10 },
                new KitOption { CdProduto = 14172, DsProduto = "KIT BIOPSIA DE MAMA", CdFormula = 11 },
                new KitOption { CdProduto = 14171, DsProduto = "KIT BIOPSIA DE PROSTATA", CdFormula = 12 },
                new KitOption { CdProduto = 14520, DsProduto = "KIT PUNCAO PORT CATH", CdFormula = 18 },
                new KitOption { CdProduto = 16103, DsProduto = "KIT ACIDO ZOLEDRONICO 4MG", CdFormula = 22 },
                new KitOption { CdProduto = 16104, DsProduto = "KIT PAMIDRONATO DE CALCIO", CdFormula = 23 },
                new KitOption { CdProduto = 16199, DsProduto = "KIT CIRURGICO DAY CLINIC", CdFormula = 25 },
                new KitOption { CdProduto = 16730, DsProduto = "KIT AGULHAMENTO", CdFormula = 30 },
            };
        }
    }
}
