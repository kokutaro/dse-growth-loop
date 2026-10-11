"""開発費用設定の検証とBicepへの変換。AI台帳の実行時制御へ渡す設定。"""
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
    period = env.get('AI_BUDGET_PERIOD', 'Daily')
    if period not in ('Daily', 'Weekly'):
        raise ValueError('AI_BUDGET_PERIOD は Daily / Weekly を指定してください。')
    weekly_calls = integer(env, 'AI_WEEKLY_CALL_LIMIT', 0, maximum=1680)
    weekly_cost = decimal_value(env, 'AI_WEEKLY_TOTAL_JPY_LIMIT', '0')
    infra_reserve = decimal_value(env, 'AI_INFRASTRUCTURE_RESERVE_JPY', '0')
    external_reserve = decimal_value(env, 'AI_EXTERNAL_AI_RESERVE_JPY', '0')
    if period == 'Weekly' and (Decimal(weekly_cost) > 2000 or Decimal(fx) != 162 or
            Decimal(infra_reserve) + Decimal(external_reserve) >= Decimal(weekly_cost)):
        raise ValueError('週次予算は2,000円以下、固定為替162円、予算内の留保が必要です。')
    money_guard = (budget and calls and Decimal(cost) > 0) if period == 'Daily' else (weekly_calls and Decimal(weekly_cost) > 0)
    if not stopped and not (money_guard and tokens and Decimal(fx) > 0 and price_version and fx_version):
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
        'AiUsage__BudgetPeriod': period,
        'AiUsage__WeeklyCallLimit': str(weekly_calls),
        'AiUsage__WeeklyTotalJpyLimit': weekly_cost,
        'AiUsage__InfrastructureReserveJpy': infra_reserve,
        'AiUsage__ExternalAiReserveJpy': external_reserve,
    }
    for name, default, ceiling in [('OriginalBytes', 10485760, 10485760), ('ExpandedBytes', 52428800, 52428800), ('Entries', 10000, 10000), ('ProcessingSeconds', 120, 120), ('Attempts', 3, 3)]:
        runtime['Ingestion__' + name] = str(integer(env, 'Ingestion__' + name, default, 1, ceiling))
    mode = env.get('Ai__Mode', 'Mock')
    if mode not in ('Mock', 'Live'):
        raise ValueError('Ai__Mode は Mock / Live を指定してください。')
    runtime['Ai__Mode'] = mode
    runtime['AiUsage__LiveOptIn'] = str(boolean(env, 'AiUsage__LiveOptIn', 'false')).lower()
    for name, default, ceiling in [('MaxInputTokens', 8000, 16000), ('MaxOutputTokens', 1000, 8000), ('MaxConcurrency', 1, 8), ('MaxAttempts', 2, 3), ('TimeoutSeconds', 30, 120)]:
        runtime['AiUsage__' + name] = str(integer(env, 'AiUsage__' + name, default, 1, ceiling))
    # 明示allowlist。キー本文やsmoke承認を通常配置へコピーしない。
    for key in ('Foundry__ModelId', 'Foundry__ModelVersion', 'Foundry__Region', 'Foundry__Sku',
                'Foundry__OutputLimit', 'Foundry__InputUsdPerMillion', 'Foundry__OutputUsdPerMillion',
                'Jev__Model', 'Jev__SecretVersion', 'Jev__InputUsdPerMillion', 'Jev__OutputUsdPerMillion', 'Jev__OutputLimit'):
        if env.get(key):
            runtime[key] = env[key]
    return {
        'approvedMonthlyBudgetJpy': budget,
        'budgetEmails': emails,
        'budgetStartDate': f'{start}T00:00:00Z' if budget else '',
        # Queue消費Workerは明示opt-in。既定は未配置。
        'deployIngestionWorker': boolean(env, 'DEPLOY_INGESTION_WORKER', 'false'),
        'deployBusinessAnalysisJob': boolean(env, 'DEPLOY_BUSINESS_ANALYSIS_JOB', 'false'),
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
