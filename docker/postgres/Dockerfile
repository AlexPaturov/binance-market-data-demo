# Postgres + pg_cron. База Debian (не alpine): пакет postgresql-16-cron ставится готовым,
# без сборки из исходников. Переход с alpine безопасен только на ЧИСТОЙ инициализации —
# коллация БД задаётся C.UTF-8 (байтовый порядок, одинаковый на любой libc), поэтому
# текстовые индексы больше не привязаны к musl/glibc.
FROM postgres:16

RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-16-cron \
    && rm -rf /var/lib/apt/lists/*

# Каталог холодного tablespace с правами postgres. Именованный том наследует эту структуру
# при первом монтировании (нужно demo). Bind-mount (dev/prod) перекрывает её каталогом хоста,
# поэтому там правка ни на что не влияет — cold-директорию готовит хост.
RUN mkdir -p /mnt/pg_tablespaces/cold \
    && chown -R postgres:postgres /mnt/pg_tablespaces
