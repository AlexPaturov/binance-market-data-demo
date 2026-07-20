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

### Пароль Seq остался в истории гита

Файл `src/BinanceDataCollector.Worker/docs/miscelanious.md` содержал логин и пароль от Seq открытым текстом. Файл удалён, но **в истории он остаётся** (коммит `402e0e2`). Пароль надо **сменить** — удаление файла его не отзывает.

### Секреты из `launchSettings.json` Worker остались в истории гита

`src/BinanceDataCollector.Worker/Properties/launchSettings.json` снят с отслеживания (`git rm --cached`) и теперь попадает под правило `.gitignore` `**/Properties/launchSettings.json` — как у DataManager. Локальный файл остаётся на диске.

Пока он трекался, в историю попали пароль БД (`bindatacoll`) и пароль RabbitMQ. Их надо **сменить** — снятие файла с отслеживания старые значения из истории не отзывает.
