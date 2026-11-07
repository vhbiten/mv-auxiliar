using System;

namespace SoulmvKit.Core
{
    // Quem vê o quê, decidido pelo prefixo do login do MV Soul: farmacêutico entra com
    // FA e atendente de farmácia com AF.
    //
    // A regra é por BLOQUEIO, não por liberação: só o prefixo do atendente perde
    // módulo. Login de outro setor, conta de teste ou o usuário "PREVIEW" das flags de
    // screenshot continuam vendo tudo — um prefixo novo não pode nascer sem acesso.
    //
    // Isto organiza o menu; não é barreira de segurança. O MV continua sendo quem
    // autoriza cada operação no servidor.
    public static class Permissoes
    {
        // Prefixo do login dos atendentes de farmácia (ex.: AF1122).
        private const string PrefixoAtendente = "AF";

        public static bool EhAtendenteFarmacia(string usuario)
        {
            if (string.IsNullOrEmpty(usuario)) return false;
            return usuario.Trim().StartsWith(PrefixoAtendente, StringComparison.OrdinalIgnoreCase);
        }

        // Módulos de reposição: Previsão de Consumo (a Central pede à CAF) e Estoque
        // (a CAF decide o que comprar). Os dois são decisão de farmacêutico.
        public static bool PodeVerReposicao(string usuario)
        {
            return !EhAtendenteFarmacia(usuario);
        }
    }
}
