# CommonModules — восстановление XML

Репозиторий: `konturweld-sys/Service-1C-Email-Telegram`, ветка `main`.

## Структура

Каждый общий модуль представлен метафайлом `CommonModules/<Имя>.xml` и текстом `CommonModules/<Имя>/Ext/Module.bsl`. BSL-файлы в `Project/1C_Modules/` оставлены без удаления.

## Формат XML

Файлы используют структуру выгрузки метаданных 1С: `MetaDataObject/CommonModule/Properties`, namespace MDClasses, namespace-префиксы 1С для core/readable/current-config, порядок и набор свойств CommonModule, а также версию формата 2.16. Для общих модулей `ChildObjects` и `InternalInfo` не используются. Состав свойств и расположение BSL соответствуют спецификации CommonModule.

## Зафиксированные свойства

Для всех семи модулей:
- Global=false
- ClientManagedApplication=false
- Server=true
- ClientOrdinaryApplication=false
- ServerCall=false
- Privileged=false
- ReturnValuesReuse=DontUse

Только `ЕРП_ИнтеграцияЛогиCOM`: ExternalConnection=true. Для остальных ExternalConnection=false.

## Идентификаторы и границы достоверности

История диалога подтверждает, что пользователь присылал семь XML 16.11.2026, и сохраняет только окончания UUID: 5101, 5201, 5301, 5401, 5501, 5601, 5701. Полные исходные UUID и исходные значения Synonym/Comment из доступной истории восстановить не удалось.

Поэтому в текущих XML оставлены технические UUID, ранее назначенные при аварийном восстановлении (префикс `44444444-4444-4444-8444-44444444`, окончания 5101–5107). Они НЕ являются подтверждёнными исходными UUID. Synonym и Comment оставлены пустыми, поскольку исходные значения не восстановлены. Перед импортом в ERP требуется сверить/заменить UUID и заполнить Synonym/Comment по исходной конфигурации. Побайтная идентичность исходным XML не заявляется.

## Не выполнялось

Импорт в 1С, запуск Конфигуратора, сборка и тестирование не выполнялись.
