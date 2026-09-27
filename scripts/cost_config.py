"""開発費用設定の検証とBicepへの変換。AIの実行時制御は後続アダプターの契約。"""
from datetime import date
from decimal import Decimal, InvalidOperation
import re


def integer(env, key, default, minimum=0, maximum=2147483647):
    try:
        value = int(env.get(key, str(default)))
    except ValueError:
        raise ValueError(f'{key} は整数で指定してください。') from None
    if not minimum <= value <= maximum:
        raise ValueError(f'{key} は {minimum}〜{maximum} で指定してください。')
    return value


def decimal_value(env, key, default, minimum=Decimal('0')):
    try:
        value = Decimal(env.get(key, default))
    except InvalidOperation:
        raise ValueError(f'{key} は数値で指定してください。') from None
    if not value.is_finite() or value < minimum:
        raise ValueError(f'{key} は {minimum} 以上の有限数で指定してください。')
    return str(value)


def boolean(env, key, default):
    value = env.get(key, default).lower()
    if value not in ('true', 'false'):
        raise ValueError(f'{key} は true / false で指定してください。')
    return value == 'true'


def parameters(env):
    budget = integer(env, 'APPROVED_MONTHLY_BUDGET_JPY', 0)
    emails = [s.strip() for s in env.get('AZURE_BUDGET_EMAILS', '').split(',') if s.strip()]
    start = env.get('AZURE_BUDGET_START_DATE', '')
    if budget:
        if env.get("AZURE_BUDGET_CURRENCY", "JPY") != "JPY":
            raise ValueError("このBudgetは請求通貨JPY専用です。外貨額を円として指定できません。")
        if not emails or len(emails) > 5 or any(not re.fullmatch(r'[^\s@]+@[^\s@]+\.[^\s@]+', s) for s in emails):
            raise ValueError('承認予算には AZURE_BUDGET_EMAILS（1〜5件）が必要です。')
        try:
            parsed = date.fromisoformat(start)
        except ValueError:
            raise ValueError('AZURE_BUDGET_START_DATE を YYYY-MM-01 で指定してください。') from None
        if parsed.day != 1:
            raise ValueError('Budget開始日は月初を指定してください。')
    stopped = boolean(env, 'AI_STOPPED', 'true')
    tokens = integer(env, 'AI_DAILY_TOKEN_LIMIT', 0)
    calls = integer(env, 'AI_DAILY_CALL_LIMIT', 0)
    cost = decimal_value(env, 'AI_DAILY_ESTIMATED_JPY_LIMIT', '0')
    fx = decimal_value(env, 'AI_USD_TO_JPY', '0')
    price_version = env.get('AI_PRICE_VERSION', '')
    fx_version = env.get('AI_FX_VERSION', '')
    if not stopped and not (budget and tokens and calls and Decimal(cost) > 0 and Decimal(fx) > 0 and price_version and fx_version):
        raise ValueError('AI停止解除には承認予算、正の日次上限、為替、単価版、為替版が必要です。')
    level = env.get('APP_LOG_LEVEL', 'Warning')
    if level not in ('Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None'):
        raise ValueError('APP_LOG_LEVEL が不正です。')
    runtime = {
        'Logging__LogLevel__Default': level,
        'Logging__LogLevel__Microsoft.AspNetCore': 'Warning',
        'Operations__AnalysisMode': 'Manual',
        'Operations__RecoveryMode': 'Manual',
        'Operations__WarmupEnabled': 'false',
        'AiUsage__Stopped': str(stopped).lower(),
        'AiUsage__DailyTokenLimit': str(tokens),
        'AiUsage__DailyCallLimit': str(calls),
        'AiUsage__DailyEstimatedJpyLimit': cost,
        'AiUsage__UsdToJpy': fx,
        'AiUsage__PriceVersion': price_version,
        'AiUsage__FxVersion': fx_version,
        'AiUsage__DayTimeZone': 'UTC',
    }
    for name, default, ceiling in [('OriginalBytes', 10485760, 10485760), ('ExpandedBytes', 52428800, 52428800), ('Entries', 10000, 10000), ('ProcessingSeconds', 120, 120), ('Attempts', 3, 3)]:
        runtime['Ingestion__' + name] = str(integer(env, 'Ingestion__' + name, default, 1, ceiling))
    return {
        'approvedMonthlyBudgetJpy': budget,
        'budgetEmails': emails,
        'budgetStartDate': f'{start}T00:00:00Z' if budget else '',
        # Queue消費Workerは明示opt-in。既定は未配置。
        'deployIngestionWorker': boolean(env, 'DEPLOY_INGESTION_WORKER', 'false'),
        'logsDailyQuotaGb': decimal_value(env, 'LOGS_DAILY_QUOTA_GB', '0.05', Decimal('0.023')),
        'logsRetentionDays': integer(env, 'LOGS_RETENTION_DAYS', 30, 30, 730),
        'telemetrySamplingPercentage': integer(env, 'TELEMETRY_SAMPLING_PERCENTAGE', 10, 1, 100),
        'blobSoftDeleteDays': integer(env, 'BLOB_SOFT_DELETE_DAYS', 7, 1, 365),
        'blobVersioningEnabled': boolean(env, 'BLOB_VERSIONING_ENABLED', 'true'),
        'blobHistoryReviewDays': integer(env, 'BLOB_HISTORY_REVIEW_DAYS', 30, 1),
        'acrKeepLatestTags': integer(env, 'ACR_KEEP_LATEST_TAGS', 3, 1),
        'acrMinimumImageAgeDays': integer(env, 'ACR_MINIMUM_IMAGE_AGE_DAYS', 30, 1),
        'runtimeSettings': [{'name': key, 'value': value} for key, value in runtime.items()],
    }
