# BinanceDataCollector — Auth Schema

## Контракт данных для аутентификации, авторизации и emergency-режима

> Этот документ описывает **логическую модель и контракт хранения**.
> Он не зависит от ORM и используется как источник истины для SQL-скриптов и кода.

---

## 1. Общие принципы

* External IdP (Azure AD B2C) используется **только для аутентификации**
* Все роли и permissions принадлежат **внутренней системе**
* Emergency и degraded режимы поддерживаются **данными**, а не флагами в коде

---

## 2. Сущности и ответственность

### 2.1 User

**Назначение:** внутренний пользователь системы, носитель ролей и прав.

* Один User может иметь несколько внешних идентичностей
* Блокировка пользователя осуществляется через `is_disabled`

Ключевые поля:

* user_id
* is_disabled
* created_at
* updated_at

---

### 2.2 UserIdentity

**Назначение:** связь пользователя с источником аутентификации.

Примеры:

* Azure AD B2C → `sub`
* Local Auth → `local:*`

Правила:

* `(auth_source, external_subject_id)` — уникальны
* Идентичность может быть деактивирована без удаления пользователя

Ключевые поля:

* user_identity_id
* user_id
* auth_source
* external_subject_id
* is_active

---

### 2.3 Role

**Назначение:** логическая группировка permissions.

Ключевые поля:

* role_id
* name
* description

---

### 2.4 Permission

**Назначение:** атомарное право доступа.

Примеры:

* VIEW_DASHBOARD
* RUN_JOBS
* MANAGE_USERS

Ключевые поля:

* permission_id
* code
* description

---

### 2.5 UserRole

**Назначение:** связь пользователей с ролями (M:N).

Ограничения:

* уникальность пары `(user_id, role_id)`

---

### 2.6 RolePermission

**Назначение:** связь ролей с permissions (M:N).

Ограничения:

* уникальность пары `(role_id, permission_id)`

---

### 2.7 AuthorizationSnapshot

**Назначение:** server-side кеш прав пользователя.

Используется для:

* enforcement авторизации
* работы в degraded / emergency режимах

Состояния:

* `authoritative` — IdP доступен
* `stale` — IdP недоступен

Ключевые поля:

* snapshot_id
* user_id
* permissions_payload
* state
* issued_at
* expires_at

---

### 2.8 SystemState

**Назначение:** глобальное состояние системы.

Примеры ключей:

* `AUTH_MODE` → S0 | S1 | S2
* `IDP_AVAILABLE` → true | false

Ключевые поля:

* key
* value
* updated_at
* updated_by

---

### 2.9 AuditEvent

**Назначение:** аудит security-критичных событий.

Логируются:

* login / logout
* переходы S0 / S1 / S2
* refresh authorization snapshot

Ключевые поля:

* event_id
* type
* actor_user_id
* metadata
* created_at

---

## 3. Связи (кратко)

* User 1—N UserIdentity
* User N—M Role (через UserRole)
* Role N—M Permission (через RolePermission)
* User 1—N AuthorizationSnapshot

---

## 4. Примечания по реализации

* Все таблицы создаются SQL-скриптами
* Изменения схемы — через versioned SQL
* Код не должен предполагать наличие EF Core

---

**Этот документ является обязательным к соблюдению при реализации.**
