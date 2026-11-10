# Your first trade study

A trade study compares design alternatives against weighted criteria and tells you which one wins, by how much, and
how sensitive that answer is to the weights. In this tutorial you choose a battery for the **CubeSat mission**
sample, then compare two product-line configurations. Allow about 15 minutes.

## 1. Open the sample

Project menu → **New project…** → **CubeSat mission** → **Create project**.

Open the *Trade Studies* package and its *Battery Options* diagram. There are three alternative blocks, each with the
value properties a trade needs:

| Alternative | energy (Wh) | mass (kg) | cost (EUR) | cycleLife |
| --- | --- | --- | --- | --- |
| Battery 18650 Li-ion | 40 | 0.27 | 3500 | 5000 |
| Battery LiPo Pouch | 36 | 0.20 | 2800 | 2500 |
| Battery Space-Qualified | 45 | 0.35 | 9000 | 8000 |

The sample already contains a finished *Battery Selection* study. You will build your own and compare.

## 2. Create the study

1. Select the *Trade Studies* package and click **Simulate → Variants**. In the *Variants & trades* tab, open
   **Trade studies**.
2. Click **New trade study** and rename it `My Battery Trade` in the inspector.
3. In **Alternatives**, pick the three battery blocks.

## 3. Add the criteria

Click **Add criterion** four times and fill in the rows:

| Criterion | Weight | Better | Source | Normalize |
| --- | --- | --- | --- | --- |
| `energy` | 3 | max ↑ | property | min-max |
| `mass` | 2 | min ↓ | property | min-max |
| `cost` | 2 | min ↓ | property | min-max |
| `cycleLife` | 2 | max ↑ | property | utility |

The criterion name is also the name of the value property it reads; use **Value** when the property has a different
name. Weights are relative: 3, 2, 2, 2 means energy counts for a third of the score.

For `cycleLife`, enter the utility curve `2000:0, 5000:0.9, 8000:1`: below 2000 cycles is worthless for a two-year
mission, 5000 cycles is nearly as good as it gets, and more brings little extra. Min-max normalisation would instead
make the best alternative score 1 and the worst 0, however close they are.

> [!NOTE]
> **TODO (screenshot):** the trade study tab with four criteria and the ranked results.

## 4. Read the result

The results rank the alternatives by weighted score:

| Rank | Alternative | Score |
| --- | --- | --- |
| 1 | Battery 18650 Li-ion | 0.66 |
| 2 | Battery Space-Qualified | 0.56 |
| 3 | Battery LiPo Pouch | 0.48 |

The space-qualified battery has the most energy and cycles, but its mass and cost cost it the lead. The table shows
each criterion's raw, normalised and weighted value, so you can see exactly where each score comes from. Your study
should match the sample's *Battery Selection* study.

## 5. Check the sensitivity

Below the results:

- **Sensitivity** multiplies each weight by 0.5 … 1.5 in turn and shows whether the winner or the ranking changes.
- **Winner changes at share** tells you, per criterion, what share of the total weight it would need before another
  alternative wins. If that share is far from today's, the decision is robust; if it is close, discuss the weights
  with the team before committing to it.

Try raising the `energy` weight to 6: the space-qualified battery takes the lead (about 0.67 against 0.61). Set it
back to 3.

## 6. Record the decision

1. **Export CSV** to attach the decision matrix to a design review.
2. Write the rationale in the study's **Documentation** in the inspector, so the reason travels with the decision.
3. **Commit** with the message `Battery trade`.

The trade study is a model element, so it is versioned, merged and reviewed with the rest of the design.

## 7. Bonus: compare configurations of a product line

Trade studies also compare **configurations** of a product line.

1. In the *Variants & trades* tab, open **Configurations** and click **Add example product line**. It adds a car
   product line with *Eco* and *Touring* configurations; its features carry `cost` and `mass` tags.
2. In **Trade studies**, create a new study with *Eco* and *Touring* as alternatives.
3. Add criteria `cost` (min ↓) and `mass` (min ↓). For a configuration, a criterion's value is the sum of that tag over
   the selected features, so each configuration gets its total cost and mass.

## 8. Bonus: criteria computed by parametrics

Set a criterion's **Source** to *parametric* to rank blocks by a value the [parametric solver](../docs/simulation.md#parametrics)
computes, such as a power margin or an acceleration time, instead of a value typed in by hand.
