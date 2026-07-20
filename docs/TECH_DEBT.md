# Технический долг

Что известно и не сделано. Только живые пункты — закрытое отсюда убирается.

---

## 1. Секреты

### Пароль Seq захардкожен в `deploy.yml`

```yaml
SEQ_ADMIN_USER=lex
SEQ_ADMIN_PASS=lex
```

Должно быть в GitHub Secrets (как уже сделано для Grafana и Telegram).
