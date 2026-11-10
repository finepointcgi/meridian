# Requirements to verification

This tutorial follows requirements from writing to verified, using the **Vehicle sample**: find the gaps, add and
derive a requirement, link it to the design, plan and record verification, deal with a changed requirement, round-trip
with a spreadsheet, get it reviewed, and publish a baseline with a verification report. Allow about 30 minutes.

## 1. Open the sample and find the gaps

1. Project menu → **New project…** → **Vehicle sample** → **Create project**.
2. Click **Traceability → Coverage**. The *Output* tab reports how many requirements are satisfied and verified, and
   lists the unverified ones.
3. Click **Traceability → Table** and tick **Not verified**. Two leaf requirements remain: *REQ-003 Range* and
   *REQ-005 Engine Power*. Both are satisfied (by *FuelTank* and *Engine*) but nothing verifies them.

> [!NOTE]
> **TODO (screenshot):** the traceability table filtered to *Not verified*.

## 2. Add a requirement

1. Click **Requirements → Table** to open the requirements table, then **New requirement**. It gets the next id,
   `REQ-006`.
2. Name it `Fuel Economy` and set its text: `The vehicle shall consume at most 6 l/100 km in the WLTP cycle.`
3. In the inspector's **Requirement attributes**, set priority *high*, status *proposed* and verification method
   *test*. Under **Custom attributes** add `Source` = `Marketing brief`.

## 3. Derive it from its parent need

1. Open the *Vehicle Requirements* diagram and drag *Fuel Economy* from the model explorer onto it.
2. Pick **Derive** in the shapes panel and drag from *Fuel Economy* to *Range*: fuel economy is derived from the range
   requirement.

*Range* now lists *Fuel Economy* under **derived**, and the traceability table shows the link in the *Derived* column.

## 4. Satisfy it from the traceability table

1. In the traceability table, clear the filters and find the *REQ-006* row.
2. In its *Satisfied by* cell, open **Link…** and choose *Engine*. The picker only offers elements that can satisfy a
   requirement and are not linked yet.

## 5. Plan the verification in the matrix

1. Select the *Verification* package and click **Requirements → Test case** three times. Rename the new test cases
   `Range Drive`, `Dyno Power` and `WLTP Cycle`.
2. Click **Traceability → Matrix**. Choose **Rows**: *Test Cases*, **Columns**: *Requirements*, **Relationship**: *Verify*.
3. Click the empty cells *Range Drive × Range*, *Dyno Power × Engine Power* and *WLTP Cycle × Fuel Economy*. Each click
   creates a verify relationship.

Back in the traceability table, the *Not verified* filter now shows nothing.

> [!NOTE]
> **TODO (screenshot):** the traceability matrix with the three new verify cells.

## 6. Record results

1. Select *Range Drive* and set **Verdict** to *pass* in the inspector. Set *Dyno Power* to *pass* and *WLTP Cycle* to
   *fail* (the prototype used 6.4 l/100 km).
2. In the traceability table switch to **Multi-level**. Each chain runs *need → requirement → derived → design → test*
   with the verdict; the failing chain stands out.

## 7. Handle a changed requirement

1. Change the text of *REQ-002 Acceleration* to `… in less than 7.5 s.`
2. Its links (the satisfy links from *Vehicle* and the *accel* constraint property, and the verify link from
   *Acceleration Test*) are now **suspect**: the design and the test were checked against the old text.
3. In the traceability table tick **Has suspect links**, review each link and click its suspect marker to clear it
   once you are satisfied. Validation (**F7**) lists suspect links as TRC-001 warnings until then.

## 8. Round-trip with a spreadsheet

1. **Requirements → Exchange → Export Excel (.xlsx)** downloads the requirements with all attributes, including an
   `Attr: Source` column.
2. Open the file, change the priority of *REQ-004 Braking* to `critical`, and save it.
3. **Requirements → Exchange → Re-import Excel / CSV with preview…** and choose the file. The preview lists one
   changed row with the old and new priority. Accept it and apply: it is one undo step.

## 9. Get it reviewed

1. Select the *Requirements* package and click **Review → Request**.
2. Title it `Requirements review`, choose **Package** as the scope, compare against **Commit a new baseline now**,
   add reviewers (on a server) and tick **When approved, set the in-scope requirements' status to "approved"**.
3. Reviewers work through **Review → Reviews**: they tick items with **Mark as reviewed**, read the **Changes** tab,
   discuss in **Discussion** and **Approve**. When everyone has approved, **Mark approved** sets the requirements'
   status to *approved*.

In the desktop app's local workspace, the same steps give you a personal review checklist.

## 10. Baseline and report

1. **Commit** with the message `Requirements reviewed`.
2. Project menu → **Baselines…** → **New baseline**, name `SRR`, on the version you just committed.
3. **Generate → Formats → Word / PDF document from template…**, pick **Verification & validation report**, choose
   PDF and **Generate**. The document contains the requirements, the traceability coverage and the validation
   results as of now.

Later, compare the `SRR` baseline with the current model in **Baselines…** to see exactly what changed since the
review.
