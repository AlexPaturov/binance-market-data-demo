# Auth MVP Plan

## Цель MVP

Не строить полноценную IAM-систему.

Для портфолио достаточно: закрыть DataManager и Hangfire Dashboard, иметь 2-3 роли, показать claims/role-based authorization и описать модель доступа.

## MUST HAVE

1. Определить минимальные роли:
   - `Admin` - полный доступ, Hangfire, управление архивами/jobs.
   - `Operator` - запуск/просмотр рабочих операций без админских настроек.
   - `Viewer` - только чтение dashboard/status/database info; выдается по умолчанию authenticated users без explicit role claim.

2. Выбрать один источник identity:
   - Azure B2C должен выпускать elevated роли `Operator`/`Admin` в token claims;
   - локальный allowlist пользователей из config не использовать как MVP-решение;
   - если role claim отсутствует, приложение назначает `Viewer` по умолчанию.

   Для портфолио лучше не усложнять: один провайдер, один flow, без scopes-heavy модели.

3. Включить middleware в `DataManager`:
   - `UseAuthentication()`;
   - `UseAuthorization()`;
   - вернуть корректный `UseCookiePolicy()` только если он реально нужен для OIDC.

4. Закрыть MVC endpoints:
   - весь DataManager требует авторизации по умолчанию;
   - `Home/Index`, Inspector, Archive - минимум `Viewer`;
   - действия запуска/импорта/удаления архивов - `Operator` или `Admin`;
   - admin-only операции - только `Admin`.

5. Закрыть Hangfire Dashboard:
   - убрать `AllowAllConnectionsFilter`;
   - заменить на фильтр, который пускает только authenticated user с ролью `Admin`.

6. Привести claims -> roles:
   - явно описать, из какого claim берется роль;
   - если built-in B2C user flow не отдает роли, настроить custom attribute/custom policy/claim issuance в B2C;
   - не смешивать одновременно роли, permissions, scopes, если они не нужны.

7. Добавить health/demo-safe поведение:
   - `/health/live` можно оставить anonymous;
   - `/health/ready` решить явно: либо anonymous для Docker healthcheck, либо internal-only через сеть;
   - все UI/dashboard routes закрыты.

8. Обновить документацию:
   - `docs/common/auth/...` сократить до фактической MVP-модели;
   - добавить roles matrix;
   - явно написать, что advanced scopes/permissions не входят в MVP.

## SHOULD HAVE

1. Один integration test на authorization:
   - anonymous user получает redirect/401;
   - `Viewer` видит read-only page;
   - `Viewer` не может запускать archive/job action;
   - `Admin` видит Hangfire.

2. Один auth setup пример без секретов:
   - какие B2C token claims должны приходить;
   - пример decoded token claims с `Viewer`, `Operator`, `Admin` без реальных user identifiers.

3. UI-индикатор текущего пользователя:
   - email/name;
   - роль;
   - logout button.

## DO NOT DO NOW

- Не делать полноценную permission/scopes matrix.
- Не строить пользовательскую админку управления пользователями.
- Не делать регистрацию пользователей.
- Не добавлять refresh tokens, custom token storage, distributed session storage.
- Не внедрять IdentityServer/ASP.NET Core Identity, если уже выбран Azure B2C/OIDC.
- Не защищать каждый маленький endpoint индивидуально, если можно задать default authorization policy.
- Не чинить весь auth-долг из старых `.odt` документов.

## Минимальный порядок работ

1. Принять решение: Azure B2C/OIDC emits `Viewer`, `Operator`, `Admin` token claims.
2. Включить authentication/authorization middleware.
3. Настроить default policy: UI закрыт по умолчанию.
4. Добавить normalization of B2C role claims to ASP.NET roles.
5. Повесить роли на MVC actions/controllers.
6. Заменить Hangfire auth filter.
7. Проверить руками 3 сценария: anonymous, viewer, admin.
8. Добавить 2-4 auth tests.
9. Обновить README/auth docs.
10. Остановиться.

## STOP CONDITION

Система авторизации считается достаточной для портфолио, когда:

- anonymous не попадает в DataManager UI;
- Hangfire доступен только `Admin`;
- `Viewer`, `Operator`, `Admin` имеют разные видимые возможности;
- роли назначаются воспроизводимо в B2C и приходят в OIDC token claims;
- есть короткая roles matrix в markdown;
- есть хотя бы базовые tests или documented manual verification;
- в коде нет `AllowAllConnectionsFilter` на production dashboard.
