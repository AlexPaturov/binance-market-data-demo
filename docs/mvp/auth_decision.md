# Auth MVP Decision

## Решение

Для portfolio MVP оставляем существующий подход на Azure B2C / OIDC.

Локальную cookie-only авторизацию не вводим как основной путь, потому что Azure B2C уже добавлен в проект и почти доведен до рабочего состояния.

## Цель

Довести авторизацию до минимально демонстрируемого состояния:

- DataManager закрыт от anonymous users.
- Hangfire Dashboard доступен только `Admin`.
- Есть минимальная ролевая модель.
- Поведение воспроизводимо локально и понятно из документации.

## Auth Flow

Основной flow:

1. Пользователь открывает DataManager.
2. ASP.NET Core делает challenge в Azure B2C через OpenID Connect.
3. После успешного входа пользователь получает application cookie.
4. Приложение нормализует role claims из token в ASP.NET roles.
5. Authorization policies ограничивают доступ к UI и операциям.

## MVP Roles

- `Viewer` - чтение dashboard/status/database info; default role for authenticated users without an explicit B2C role claim.
- `Operator` - чтение + запуск рабочих операций, например archive import/jobs.
- `Admin` - полный доступ, включая Hangfire Dashboard.

## Role Mapping

Предпочтительный вариант:

- использовать role/group claim из Azure B2C, если он уже доступен в token claims.

MVP requirement:

- roles must be emitted by the identity provider as token claims;
- DataManager accepts elevated roles from B2C/OIDC token claims, not local appsettings allowlists;
- authenticated users without an explicit role claim get the `Viewer` role by default;
- if built-in B2C user flows cannot emit roles directly, use a B2C custom attribute or custom policy that produces `Viewer`, `Operator`, `Admin` claims.

Local config allowlists are no longer accepted for the senior portfolio MVP.

## MUST HAVE

- Включить `UseAuthentication()` и `UseAuthorization()` в `DataManager`.
- Настроить default authorization policy: весь DataManager UI требует authenticated user.
- Оставить `/health/live` доступным anonymous для infrastructure checks.
- Оставить `/health/ready` доступным anonymous для Docker healthcheck и Uptime Kuma; ответ не должен раскрывать чувствительные детали.
- Закрыть archive/job mutation actions минимум ролью `Operator`.
- Закрыть admin-only actions ролью `Admin`.
- Заменить `AllowAllConnectionsFilter` для Hangfire Dashboard на фильтр с проверкой `Admin`.
- Добавить минимальную roles matrix в документацию.
- Добавить ручной verification checklist.

## SHOULD HAVE

- Integration tests:
  - anonymous user получает redirect/challenge;
  - `Viewer` видит read-only page;
  - `Viewer` не может запускать mutation action;
  - `Admin` получает доступ к Hangfire.

- UI-индикатор:
  - имя/email текущего пользователя;
  - текущая роль;
  - logout action.

## DO NOT DO NOW

- Не делать регистрацию пользователей.
- Не делать пользовательскую админку.
- Не внедрять ASP.NET Core Identity.
- Не строить полноценную permission/scopes matrix.
- Не делать управление ролями из UI.
- Не добавлять refresh token storage.
- Не усложнять multi-tenant сценарии.
- Не чинить все старые auth-документы до реализации MVP.

## Manual Verification Checklist

Перед переходом к тестам проверить вручную:

- anonymous user не попадает в DataManager UI;
- login через Azure B2C проходит успешно;
- пользователь без роли не получает лишний доступ;
- `Viewer` видит read-only страницы;
- `Viewer` не может запускать archive/job actions;
- `Operator` может запускать рабочие операции;
- `Operator` не видит Hangfire Dashboard;
- `Admin` видит Hangfire Dashboard;
- logout завершает application session;
- health endpoints работают в выбранном режиме.

## STOP CONDITION

Auth MVP считается завершенным, когда:

- DataManager закрыт default policy;
- роли `Viewer`, `Operator`, `Admin` реально используются в authorization policies;
- Hangfire доступен только `Admin`;
- B2C role claim setup описан и воспроизводим;
- health endpoints не ломают Docker/monitoring;
- manual verification checklist пройден;
- есть минимальные auth tests или отдельный documented manual verification result.
