"""E8 — 障害復旧・可用性の純粋メトリクス計算 (#246).

I/O を持たない算術のみ。ライブ計測ハーネス（s16_resilience_rto）が docker stop/start と負荷の観測値を
渡し、ここで RTO / 復旧後データ損失を導く。
"""

from __future__ import annotations


def rto_seconds(down_at: float, healthy_at: float) -> float:
    """Recovery Time Objective 実測: サービス停止時刻 → 再 healthy 時刻（秒）。負値は 0 にクランプ。"""
    return max(0.0, healthy_at - down_at)


def data_loss_ratio(sent: int, persisted: int) -> float:
    """(sent − persisted) / sent。sent<=0 は 0、persisted>sent（重複等）も 0 にクランプ。"""
    if sent <= 0:
        return 0.0
    return max(0.0, (sent - persisted) / sent)


def build_e8_metrics(*, down_at: float, healthy_at: float,
                     phase2_sent: int, phase2_persisted: int) -> dict:
    """E8 の gate メトリクス。data_loss_under_outage = 復旧後（phase2）に新規投入した分の損失率
    （store-and-forward / 再接続後の publish が失われないこと）。rto_seconds は report。"""
    return {
        "data_loss_under_outage": round(data_loss_ratio(phase2_sent, phase2_persisted), 6),
        "rto_seconds": round(rto_seconds(down_at, healthy_at), 2),
    }
