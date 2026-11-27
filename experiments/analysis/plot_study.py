"""Render published block-level study summaries as labelled report figures."""

import argparse
import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

SCENARIOS = ("W1-low", "W1-high", "W2", "W4")
GROUPS = "ABCD"
COLORS = ("#2d5eaa", "#a26412", "#087c70", "#854875")


def setup():
    plt.rcParams.update({
        "font.family": "DejaVu Sans", "font.size": 10, "axes.titlesize": 11,
        "axes.spines.top": False, "axes.spines.right": False, "axes.edgecolor": "#a5b0bb",
        "axes.labelcolor": "#182637", "xtick.color": "#35485c", "ytick.color": "#35485c",
        "figure.facecolor": "white", "savefig.facecolor": "white",
    })


def finish(fig, path):
    fig.savefig(path, dpi=180, bbox_inches="tight", pad_inches=.14,
                metadata={"Software": f"Matplotlib {matplotlib.__version__}"})
    plt.close(fig)


def dots(ax, x, points, color):
    offsets = [(i / max(1, len(points) - 1) - .5) * .26 for i in range(len(points))]
    ax.scatter([x + width for width in offsets], points, s=21, alpha=.65, color=color, zorder=3)


def comparisons(data, metric, title, units, output):
    fig, axes = plt.subplots(2, 2, figsize=(8.6, 5.6), layout="constrained", sharey=True)
    for ax, scenario in zip(axes.flat, SCENARIOS):
        ax.set_title(scenario, loc="left", fontweight="bold")
        for i, (g, color) in enumerate(zip(GROUPS, COLORS)):
            cell = data["scenario"][scenario][g]
            pts = [p for p in cell["points"][metric] if p is not None]
            dots(ax, i, pts, color)
            summary = cell["distributions"][metric]
            if pts:
                ax.errorbar(i, summary["mean"], yerr=summary["halfWidth"], fmt="D", markersize=4.5,
                            capsize=3, elinewidth=1.1, color="#172334", zorder=4)
            else:
                ax.text(i, .02, "NA", ha="center", transform=ax.get_xaxis_transform())
        ax.set_xticks(range(4), list(GROUPS))
        ax.set_xlim(-.5, 3.5)
        ax.grid(axis="y", alpha=.18)
    fig.supylabel(units)
    fig.suptitle(title + f"  |  {data['completeBlocks']} {data['phase']} blocks", fontweight="bold")
    finish(fig, output)


def duplicate_and_exposure(data, output):
    fig, axes = plt.subplots(1, 2, figsize=(8.6, 3.5), layout="constrained", sharey=True)
    for ax, scenario in zip(axes, ("W2", "W4")):
        cells = [data["scenario"][scenario][g] for g in GROUPS]
        rates = [100 * c["counts"]["duplicateLogical"] / max(1, c["counts"]["dispatched"]) for c in cells]
        bars = ax.bar(list(GROUPS), rates, color=COLORS)
        ax.bar_label(bars, labels=[f"{v:.2f}%" for v in rates], padding=3, fontsize=8)
        ax.set_title(scenario, loc="left", fontweight="bold")
        ax.set_ylim(0, max(1, axes[0].get_ylim()[1], max(rates) * 1.25))
        ax.grid(axis="y", alpha=.18)
    fig.supylabel("Duplicate effects by logical request (%)")
    fig.suptitle(f"Duplicate logical requests per dispatched request  |  {data['phase'].capitalize()}", fontweight="bold")
    finish(fig, output)


def costs(data, output):
    fig, axes = plt.subplots(1, 2, figsize=(8.6, 3.8), layout="constrained")
    for metric, divisor, scale, title, ax in (
        ("internalRetries", "dispatched", 1000, "Internal retries per 1,000 dispatched", axes[0]),
        ("lockWaiterSamples", "resourceSamples", 100, "Samples with a PostgreSQL lock waiter (%)", axes[1]),
    ):
        for i, (g, color) in enumerate(zip(GROUPS, COLORS)):
            values = [data["scenario"][s][g]["counts"] for s in SCENARIOS]
            ax.bar([j + (i - 1.5) * .18 for j in range(4)],
                   [scale * x[metric] / max(1, x[divisor]) for x in values], width=.17, color=color, label=g)
        ax.set_xticks(range(4), SCENARIOS, rotation=18)
        ax.set_title(title, loc="left", fontweight="bold", fontsize=9.5)
        ax.grid(axis="y", alpha=.18)
    handles, labels = axes[0].get_legend_handles_labels()
    fig.legend(handles, labels, ncols=4, frameon=False, loc="outside lower center", fontsize=8)
    fig.suptitle(f"Transaction costs  |  {data['phase'].capitalize()}", fontweight="bold")
    finish(fig, output)


def effects(data, output):
    fig, axes = plt.subplots(1, 2, figsize=(8.6, 3.8), layout="constrained", sharey=True)
    for scenario, ax in zip(SCENARIOS[:2], axes):
        categories = data["factorial"][scenario]["correctlyConfirmedTps"]
        ax.axhline(0, color="#8798a9", linestyle="--", linewidth=.8)
        for i, (kind, color) in enumerate(zip(("occ", "idempotency", "interaction"), COLORS)):
            sample = categories[kind]
            pts = [p["value"] for p in sample["points"]]
            dots(ax, i, pts, color)
            error = sample["interval"]
            if pts:
                ax.errorbar(i, error["mean"], yerr=error["halfWidth"], fmt="D", markersize=4.5,
                            capsize=3, elinewidth=1.1, color="#172334")
        ax.set_xticks(range(3), ["OCC", "Persistent key", "Interaction"])
        ax.set_title(scenario, loc="left", fontweight="bold")
        ax.grid(axis="y", alpha=.18)
    fig.supylabel("Change in correctly confirmed requests/s")
    fig.suptitle("Within-block factorial effects and 95% intervals", fontweight="bold")
    finish(fig, output)


def scenario_contrast(data, output):
    fig, ax = plt.subplots(figsize=(8.0, 3.5), layout="constrained")
    series = [data["factorial"][s]["correctlyConfirmedTps"]["interaction"] for s in ("W2", "W4")] + [data["rq3"]]
    ax.axhline(0, color="#8798a9", linestyle="--", linewidth=.8)
    for i, item in enumerate(series):
        values = [x["value"] for x in item["points"]]
        dots(ax, i, values, COLORS[i])
        ci = item["interval"]
        if values:
            ax.errorbar(i, ci["mean"], yerr=ci["halfWidth"], fmt="D", color="#172334", capsize=4, zorder=4)
    ax.set_xticks([0, 1, 2], ["W2 interaction", "W4 interaction", "Paired W4 − W2"])
    ax.set_ylabel("Correctly confirmed requests/s")
    ax.grid(axis="y", alpha=.18)
    ax.set_title(f"Scenario interaction  |  {data['rq3']['classification'].capitalize()}", loc="left", fontweight="bold")
    finish(fig, output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("analysis", type=Path)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    data = json.loads(args.analysis.read_bytes())
    if data["phase"] not in ("pilot", "formal") or data["verifiedRuns"] != 16 * data["completeBlocks"]:
        raise ValueError("Use verified complete pilot or formal analysis")
    args.directory.mkdir(parents=True, exist_ok=True)
    setup()
    def out(label):
        return args.directory / (label + ".png")
    comparisons(data, "correctlyConfirmedTps", "Correctly confirmed logical throughput", "Confirmed requests/s", out("Throughput"))
    duplicate_and_exposure(data, out("Duplicate_Effects"))
    if data["phase"] == "formal":
        comparisons(data, "p95Ms", "Logical P95 for correctly confirmed requests", "Milliseconds", out("Logical_P95"))
        costs(data, out("Lock_Wait_and_Retry"))
        effects(data, out("Factorial_Effects"))
        scenario_contrast(data, out("Scenario_Interaction"))


if __name__ == "__main__":
    main()
