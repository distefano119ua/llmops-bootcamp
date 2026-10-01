PROJECT_NAME ?= llmops-bootcamp
COMPOSE := docker compose -p $(PROJECT_NAME)
COMPOSE_ALL := $(COMPOSE) --profile advanced
SERVICE ?=
TAIL ?= 100

.DEFAULT_GOAL := help
.PHONY: help up up-redis down stop restart build pull ps logs service redis redis-stop redis-ping db-shell config

help: ## Показати доступні команди
	@awk 'BEGIN { FS = ":.*## " } /^[a-zA-Z0-9_-]+:.*## / { printf "  %-14s %s\n", $$1, $$2 }' $(MAKEFILE_LIST)
	@printf '\n%s\n' 'Приклади: make up-redis | make logs SERVICE=service TAIL=50'
	@printf '%s\n' 'Назва проєкту: $(PROJECT_NAME) (можна змінити: make up PROJECT_NAME=my-project)'

up: ## Зібрати та запустити основний стек
	$(COMPOSE) up -d --build

up-redis: ## Зібрати та запустити стек разом із Redis
	$(COMPOSE_ALL) up -d --build

down: ## Зупинити та видалити контейнери й мережу, зберігши томи
	$(COMPOSE_ALL) down

stop: ## Зупинити всі контейнери, включно з Redis
	$(COMPOSE_ALL) stop

restart: ## Перезапустити наявні контейнери, включно з Redis
	$(COMPOSE_ALL) restart

build: ## Зібрати образи (можна вказати SERVICE=service)
	$(COMPOSE_ALL) build $(SERVICE)

pull: ## Завантажити готові образи
	$(COMPOSE_ALL) pull --ignore-buildable

ps: ## Показати стан усіх контейнерів, включно із зупиненими
	$(COMPOSE_ALL) ps -a

logs: ## Стежити за логами (SERVICE=service, TAIL=100)
	$(COMPOSE_ALL) logs -f --tail $(TAIL) $(SERVICE)

service: ## Перезібрати та запустити лише .NET service без залежностей
	$(COMPOSE) up -d --build --no-deps service

redis: ## Додати Redis до вже запущеного стеку
	$(COMPOSE_ALL) up -d redis

redis-stop: ## Зупинити лише Redis
	$(COMPOSE_ALL) stop redis

redis-ping: ## Перевірити Redis (очікується PONG)
	$(COMPOSE_ALL) exec redis redis-cli ping

db-shell: ## Відкрити SQL консоль Postgres
	$(COMPOSE_ALL) exec postgres psql -U llmops -d llmops

config: ## Перевірити конфігурацію Compose
	$(COMPOSE_ALL) config --quiet
