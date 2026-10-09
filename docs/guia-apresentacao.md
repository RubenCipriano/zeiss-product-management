# Guia de apresentação (PT)

Documento de estudo para explicar o projeto numa entrevista: **o que** foi feito, **como** e, sobretudo, **porquê**.
A documentação técnica "oficial" (em inglês, para os avaliadores) está no [README](../README.md) e nos
[ADRs](adr/README.md).

---

## 1. O projeto em 30 segundos

> "Fiz uma API de produtos em .NET 10 com arquitetura limpa, EF Core e PostgreSQL, e um frontend em Angular 22.
> Os pontos críticos do enunciado são dois: IDs de 6 dígitos únicos com várias instâncias, e stock correto com
> pedidos concorrentes. Resolvi-os na base de dados: uma *sequence* para os IDs e um UPDATE condicional atómico
> para o stock. Testei-os com testes de integração contra PostgreSQL real e com testes de carga em Kubernetes com
> várias réplicas. Para produção, acrescentei autenticação OIDC (Keycloak) com RBAC, rate limiting, cache Redis,
> um gateway nginx, deploy em Kubernetes separado por frontend e backend, e um servidor MCP para assistentes de IA."

---

## 2. Como foi criado (ferramentas oficiais primeiro)

Tudo começou com os geradores oficiais, e o histórico do git separa "o que a ferramenta gerou" de "o que eu mudei":

| Passo | Comando | Commit |
| --- | --- | --- |
| Solução e projetos .NET | `dotnet new sln/classlib/webapi/xunit3/globaljson/tool-manifest` | `chore: scaffold ... from official templates` |
| App Angular | `npx @angular/cli new` | o mesmo commit |
| Material / ESLint (depois substituído) | `ng add @angular/material`, `ng add angular-eslint` | `chore(frontend): add ... via ng add` |
| Tailwind | `ng add tailwindcss` | `chore(frontend): add Tailwind CSS via ng add` |
| Componentes, serviços, guard, interceptor | `ng generate ...` | dentro dos commits de feature |
| Servidor MCP | `dotnet new mcpserver` | `chore(backend): scaffold MCP server ...` |
| Migração da base de dados | `dotnet ef migrations add InitialCreate` | gerada, não escrita à mão |

Detalhe completo em [`scaffolding.md`](scaffolding.md). Para mostrar o que mudou num passo: `git show --stat <commit>`.

---

## 3. Arquitetura do backend (porque está organizado assim)

**Estrutura de pastas:** os projetos estão diretamente em `backend/` (sem `src/`), como é comum em .NET. No Visual
Studio aparecem agrupados pelas *solution folders* `Source`, `Tests` e `Mcp`.

```
Api ──► Application ──► Domain          Mcp ──(HTTP)──► Api
 └────► Infrastructure ──► EF Core / PostgreSQL / Redis
```

| Camada | O que tem | Porquê |
| --- | --- | --- |
| **Domain** | `Product`, `Category`, regras (`ProductRules`) | Regras de negócio sem dependências; testáveis sem base de dados. |
| **Application** | Casos de uso (`ProductQueryService`, `ProductCommandService`), validação, `Result`/`Error`, interfaces (ports) | Orquestração; não conhece EF Core nem HTTP. |
| **Infrastructure** | EF Core, migrações, repositórios, gerador de IDs, cache Redis | Os detalhes técnicos ficam isolados e são substituíveis. |
| **Api** | Controllers finos, HTTP (status, ETag, ProblemDetails), segurança, rate limiting | O controller só traduz HTTP ↔ caso de uso. |

**CQRS-lite:** as leituras usam projeções `AsNoTracking` diretamente para DTOs (um SELECT com JOIN, sem carregar
entidades). As escritas carregam o agregado com tracking só quando é preciso (update/delete).

**Porque não MediatR ou um repositório genérico?** Para 9 endpoints acrescentam indireção sem benefício, e o MediatR
passou a ter licença comercial. Ports específicos dizem exatamente que queries existem.

---

## 4. Os dois problemas difíceis do enunciado

### 4.1 IDs de 6 dígitos únicos "mesmo com várias instâncias"

**Solução:** uma `SEQUENCE` do PostgreSQL de 100000 a 999999, `NO CYCLE`, criada pela migração.
`nextval()` é **atómico e não transacional**: duas instâncias nunca recebem o mesmo valor, e um rollback cria um
"buraco" mas nunca reutiliza o número.

**Porque não…**
* *aleatório + retry:* paradoxo do aniversário. Com 900 mil valores, há 50% de probabilidade de colisão por volta dos
  1100 produtos.
* *`MAX(id)+1`:* corrida entre instâncias, e reutiliza IDs apagados.
* *GUID/Snowflake:* não têm 6 dígitos.
* *HiLo:* cada instância reserva blocos e um restart desperdiça o bloco inteiro, o que pesa num espaço de 900 mil.

**Prova:** um teste de integração com duas instâncias da API a criar 150 produtos em paralelo → 0 duplicados.
Também com 4 réplicas em Kubernetes durante os testes de carga.

**Limitação assumida:** a capacidade é de 900 mil produtos. Alargar para 7 dígitos é uma migração de uma linha.

### 4.2 Stock correto com pedidos concorrentes

**Solução:** cada alteração de stock é **um único UPDATE condicional**:

```sql
UPDATE products SET stock_quantity = stock_quantity - 3
WHERE id = @id AND stock_quantity >= 3
```

O PostgreSQL bloqueia a linha durante o statement e reavalia o `WHERE` com o valor mais recente. Pedidos simultâneos
ficam em fila, e cada um vê o resultado do anterior. Se a linha não for alterada (0 linhas), ou o produto não existe
(404) ou não há stock (409 com a quantidade disponível).

**A regra continua no domínio:** `Product.CanAdjustStockBy(delta)` é uma *expression tree* que o EF Core traduz para
o `WHERE`. O domínio define a regra e a base de dados aplica-a atomicamente.

**Porque não "ler → alterar → gravar" com concorrência otimista?** Sob carga, a maioria dos pedidos entraria em
conflito e teria de repetir (trabalho O(N²)), e o cliente veria erros em pedidos que podiam ter tido sucesso.

**Prova:**
* teste de integração: 60 encomendas paralelas para 25 unidades → exatamente 25 sucessos e stock 0;
* teste de carga em Kubernetes com várias réplicas: 300 encomendas para 100 unidades → exatamente 100 sucessos,
  200 × 409, stock final 0.

---

## 5. Outras decisões do backend

| Tema | O que foi feito | Porquê |
| --- | --- | --- |
| **Edição concorrente** | `xmin` do PostgreSQL como versão → `ETag`; `If-Match` no PUT/DELETE → 412 | Evita "lost updates", por exemplo um editor repor stock que já foi vendido entretanto. O `xmin` não ocupa espaço. |
| **Erros** | Erros esperados são valores (`Result<T>`); exceções só para o inesperado. Tudo em RFC 9457 com `errorCode` | Assinaturas explícitas, formato previsível; o cliente decide pelo `errorCode` sem interpretar mensagens. |
| **Validação** | FluentValidation na Application + guardas no domínio + `CHECK` na base de dados | Defesa em profundidade; a mesma fonte de limites (`ProductRules`) em todas as camadas. |
| **DTOs com `decimal?`** | Campos de valor *nullable* nos pedidos | Um campo em falta dá "is required" em vez de um `0` silencioso. |
| **Sem retry automático do EF** | `EnableRetryOnFailure` desligado | Os endpoints de stock não são idempotentes: um retry após um commit "ambíguo" aplicaria a alteração duas vezes. |
| **Paginação** | Em todas as listas (20 por defeito, máximo 100) | Listas sem limite não escalam. |
| **Migrações** | Correm num `Job` do Kubernetes (`dotnet ProductManagement.Api.dll migrate`) | Uma vez por deploy, não em cada réplica. O lock de migração do EF Core torna execuções simultâneas seguras. |
| **Seeding** | Categorias via `HasData` (vêm com o schema); produtos de exemplo via `UseSeeding` (opcional) | Dados de referência vs. dados de demonstração. |

---

## 6. Segurança (pedido para produção)

* **Autenticação:** Keycloak (OIDC). O browser faz login com **Authorization Code + PKCE** (`oidc-client-ts`). A API
  só valida o **JWT** (assinatura com as chaves do Keycloak, issuer, audience e validade) e nunca vê passwords.
* **Autorização (RBAC):** os endpoints pedem **permissões**, não roles. O mapeamento role → permissão está num só
  sítio:

  | Role | `products:read` | `stock:write` | `products:write` |
  | --- | :-: | :-: | :-: |
  | viewer | ✓ | | |
  | stock-clerk | ✓ | ✓ | |
  | manager | ✓ | ✓ | ✓ |

* **Seguro por defeito:** uma *fallback policy* exige login em qualquer endpoint novo. Só os health checks e o OpenAPI
  são anónimos, e isso está explícito no código.
* **A UI esconde, a API decide:** o frontend esconde botões que o role não pode usar (UX), mas quem garante é a API.
  Há um teste com a matriz completa 3 roles × 10 operações.
* **Rate limiting em duas camadas:** o nginx limita por IP (proteção grossa e barata) e a API por utilizador, com um
  limite mais apertado para escritas. Respondem 429 + `Retry-After`. Nota: o limitador da API é por réplica, por
  isso o limite efetivo é limite × réplicas (documentado; um limite global exigiria Redis).
* **Rede e contentores:** NetworkPolicies (só a API chega à base de dados e ao Redis, só o gateway chega à API).
  Os pods correm como não-root, com o sistema de ficheiros só de leitura e sem capabilities.

---

## 7. Cache Redis (porque "geração" e não "apagar a chave")

* Cache-aside nas leituras (`CachingProductQueries`, um decorator: a Application não sabe que existe cache).
* Cada chave inclui a **geração** do catálogo, e cada escrita faz `INCR` da geração. As entradas antigas ficam
  órfãs e expiram por TTL.
* **Porque é seguro com várias réplicas e com corridas:** o leitor lê a geração *antes* de ir à base de dados. Se
  houver uma escrita pelo meio, o resultado antigo fica guardado na geração antiga, que já ninguém lê. Com "apagar a
  chave depois de escrever", um leitor lento podia voltar a meter o valor antigo na cache.
* **Falha aberta:** se o Redis cair, vai-se à base de dados (timeouts de 500 ms). O health check fica "Degraded"
  (HTTP 200), por isso o Kubernetes não tira as réplicas de serviço.
* **Custo assumido:** qualquer escrita invalida todas as listas. Nos testes de carga, com 10% de escritas, a cache
  ajuda pouco a 1000 req/s. O próximo passo seria invalidação por produto (está nos ADRs).

---

## 8. Frontend

* **Angular 22:** componentes standalone, *zoneless*, `OnPush` por defeito e **signals** em todo o lado.
* **Estado:** o `ProductsStore` (com âmbito do ecrã) guarda o filtro e a página num signal, e o **`rxResource`** vai
  buscar os dados. Mudar o filtro cancela o pedido anterior, o que evita respostas fora de ordem. A página anterior
  mantém-se visível enquanto carrega (sem "piscar"), e os filtros iguais não repetem pedidos.
* **"Refletir alterações feitas pela API":**
  * depois de uma alteração, mostra logo a resposta do servidor e depois volta a buscar a lista;
  * além disso, refresca a cada 15 s (pausa quando o separador está escondido).
* **Design:** **Tailwind CSS** para o aspeto e **Angular CDK** para o comportamento acessível (foco nos diálogos,
  teclado nos menus). Saiu o Angular Material: bundle inicial de 121 → 91 kB.
* **Erros:** um interceptor converte tudo em `ApiError`. Os formulários mostram os erros de validação do servidor no
  campo certo, e o 412 mostra "alguém alterou este produto — Reload latest".
* **Login:** o guard manda para o Keycloak e o interceptor junta o token **só** aos pedidos `/api` (nunca a outros
  domínios). Num 401 volta a pedir login.

---

## 9. Kubernetes, gateway e réplicas

* **Um YAML e um Dockerfile por projeto** (`backend/deploy`, `frontend/deploy`), cada um com o seu `deploy.sh`, como
  se fossem repositórios separados. O contrato entre eles é só de nomes: o namespace, os serviços `api-headless` e
  `keycloak`, e a label `gateway`.
* **Escalar só o backend:** `kubectl -n product-management scale deployment/api --replicas=4`, sem mexer no
  frontend. O nginx aponta para o serviço **headless** da API com `resolve` e volta a resolver o DNS a cada 5 s. Vê
  cada pod individualmente e distribui **por pedido** (`least_conn`). Num teste, 200 pedidos ficaram
  50/50/50/50 pelas 4 réplicas.
  * *Porque não um Service normal?* O Service normal distribui por ligação TCP; com keep-alive, as ligações ficam
    "presas" aos pods antigos.
* **Gateway nginx à frente:** uma só origem para app, `/api` e `/auth` (sem CORS), cache das bundles, gzip,
  headers de segurança, rate limit por IP e retry noutra réplica se uma falhar a ligar.
* **Zero downtime:** `maxUnavailable: 0`, probes (readiness inclui a base de dados) e `preStop` de 5 s.
* **Imagens:** cada deploy gera uma tag imutável (commit + timestamp). No MicroK8s vai para o registry interno; no
  Docker Desktop usa a imagem local.
* **Dois clusters suportados:** Docker Desktop (vês tudo na app do Docker) e MicroK8s (VM Multipass).

---

## 10. Testes de carga (o que dizer sobre os resultados)

Corridos **dentro do cluster** (k6 como `Job`), numa VM com 4 vCPU:

* **150 req/s** com 2 réplicas → p95 de **3 ms**, 0 erros.
* **Correção:** 300 encomendas concorrentes para 100 unidades → **exatamente 100** vendidas, stock final 0.
* **Até ~1000 req/s:** 0 erros em 255 mil pedidos.
* **4 réplicas foi pior do que 2 → porquê?** O gargalo era o **PostgreSQL** (limitado a 1 CPU). Mais réplicas =
  mais ligações a competir pela mesma base de dados saturada → 282 erros de timeout. Dei 2 CPUs ao PostgreSQL e
  limitei o pool a 20 ligações por réplica: **0 erros** e p95 de 1,35 s → 0,78 s. O que sobra é o limite físico do
  nó, porque está tudo nos mesmos 4 vCPU.
* **Lição para a entrevista:** escalar a camada *stateless* não ajuda quando o gargalo é a base de dados. Medir
  primeiro, depois escalar a coisa certa (recursos da BD, PgBouncer, réplicas de leitura, cache mais fina).
* **Rate limit:** um utilizador a 300 req/s recebe 429 com `Retry-After` e o resto continua a funcionar.

Resultados completos: [`load-test-results.md`](load-test-results.md).

---

## 11. MCP (assistentes de IA)

* `ProductManagement.Mcp` (template oficial `dotnet new mcpserver`) expõe 8 *tools*: pesquisar, ver stock baixo,
  dar entrada/saída de stock, etc.
* **É um cliente da API, não da base de dados.** Entra com a sua própria conta de serviço (*client credentials*) com
  role `stock-clerk`, por isso a IA **não consegue** criar nem apagar produtos, e aplicam-se o mesmo RBAC, validação
  e rate limit.
* As tools têm anotações (`readOnly`, `idempotent`, ...) para o cliente pedir confirmação antes de alterações.
* `.mcp.json` também regista MCPs de desenvolvimento: Kubernetes (só leitura), PostgreSQL (modo restrito) e
  Playwright.

---

## 12. Perguntas prováveis e respostas curtas

**"Porque é que o controller não tem lógica?"**
Porque a lógica testável vive na Application e no Domain. O controller só traduz HTTP ↔ caso de uso, e todos os
endpoints tratam erros da mesma forma através do `ApiControllerBase`.

**"Como garantes que o stock não fica negativo com 10 réplicas?"**
É o PostgreSQL que garante: um UPDATE condicional atómico mais um `CHECK (stock >= 0)`. O número de réplicas não
interessa, e está provado com testes concorrentes e com o teste de carga.

**"E se o Redis cair?"**
A API continua a funcionar a partir da base de dados (falha aberta), o health fica "Degraded" e as réplicas continuam
em serviço. Há um teste para isto.

**"Porque PostgreSQL e não SQL Server?"**
Corre nativamente em todo o lado (ARM incluído) e dá-me de borla três coisas usadas aqui: `xmin` para concorrência,
sequences com limites e pesquisa por trigramas. Mudar para SQL Server só toca na Infrastructure.

**"Porque testes com base de dados real e não InMemory?"**
O que estou a testar (sequences, locks, `xmin`, `ILIKE`, cache) é específico do motor. Um fake esconderia
exatamente esses erros. Uso Testcontainers e o Respawn para limpar os dados entre testes.

**"O que farias a seguir para produção?"**
Segredos num secret manager, TLS, PostgreSQL gerido com backups e PgBouncer, OpenTelemetry com dashboards e alertas
sobre os SLOs, HPA, invalidação de cache por produto, `Idempotency-Key` no stock e GitOps (Argo CD).

**"Porque é que o `If-Match` é opcional?"**
Para a API ser fácil de experimentar (Swagger, curl). A UI envia-o sempre. Torná-lo obrigatório (428) é uma linha.

**"O que é .NET Core aqui?"**
O .NET 10 *é* o .NET Core: a Microsoft deixou cair o "Core" do nome no .NET 5. O ASP.NET Core e o EF Core mantêm-no.

---

## 13. Como fazer a demonstração (10 minutos)

1. `scripts/deploy-all.sh` → abrir http://localhost:8080 e mostrar o Docker Desktop → Kubernetes com os pods.
2. Login como `viewer`: só leitura. Logout e login como `manager`: criar, editar e ajustar stock.
3. Abrir uma edição em dois separadores e gravar nos dois → o segundo recebe o banner de conflito (412).
4. `kubectl -n product-management scale deployment/api --replicas=4` → mostrar os pods novos e que a app continua
   a funcionar.
5. `load-tests/run.sh stock-consistency` → "exatamente 100 vendidas, stock 0".
6. Swagger: tentar `DELETE` com o token do clerk → 403. Mostrar o `errorCode` nos erros.
7. (Bónus) No Claude Code: "que produtos estão com pouco stock?" → o servidor MCP responde.
