# Optional historical test inputs

The audited baseline contains 39 unique skipped historical cases. Five now execute without changing their scientific expectations: one upstream DSS sample, the regenerated pinned HYG astrometry fixture, two supplementary STC/binary-catalogue cases and the complete 674-image upstream photograph set. Their provenance differs: preserved upstream files, a regenerated adapter output and an unavailable historical local-cache hash must remain distinguishable.

The remaining 34 cases are retained. They require exact archived inputs or independent historical witnesses, rather than 34 known runtime defects. The [machine-readable inventory](optional-test-inputs.json) lists every original case, its source, required variables, missing profile bindings, expected historical payload and next action. P1/P2 in that file ranks data recovery; it is not a new bug-severity classification. A present variable also does not prove that its directory contains the expected version and resources.

## Recovery recipes and evidence limits

| Recovered cases | Reproducible input | What this establishes |
| --- | --- | --- |
| DSS metadata/tile | [Pinned survey capture](TEST_COVERAGE.md#restore-the-upstream-survey-sample) | Original published properties, overview and Orion tile satisfy the unchanged contract. Historical cache hash is unavailable. |
| Spatial star catalogue | [Pinned HYG adaptation](TEST_COVERAGE.md#restore-the-astrometry-fixture) | 109,400 valid-distance rows, original Cartesian astrometry and unchanged named-star bounds. Adapter output is regenerated; display proxies are not measured radii. |
| Two supplementary STC cases | Source-only STC/DSC snapshot, separate original binary table and HYG supplement | Missing records and later binary recovery, preserved reference positions, host identity and relocation. Keeping the binary table in the baseline directory defeats the missing-input experiment. |
| All sky photographs | [Complete original upstream capture](TEST_COVERAGE.md#restore-all-original-sky-photographs) | All 674 files, unmodified coordinates, credits and event visibility pass. The old local installation hash remains unknown. |

## Remaining input families

| Input family | Missing evidence | Next useful action |
| --- | --- | --- |
| Historical packed science and mission versions | Matching manifests, meshes, trajectories and original version/owner ordering. | Recover the complete versioned installation. A current writer round trip is a separate test, not a recovered older pack. |
| MSL/Dawn and Galileo | Complete sampled arcs/resources and the retained independent state references. | Restore all files named by the source catalogue, then run both Core and application-reference cases. A JSON without its trajectories is insufficient. |
| Script factories | Original version-17 module/resource layout and matching native state witness. | Recover the paired source/reference installation. Keep quotas, language-subset and current factory tests as separate evidence. |
| Observation, vector, globe and viewpoint fixtures | Exact historical source/packed pairs and companion numerical references or DDS pyramids. | Recover those pairs; otherwise add explicitly authored contracts for uncovered behavior while retaining the original skipped cases. |
| Recording compatibility | Original full-Moon 900-frame recording/report pair, star-point, lens and orbit/label recordings. | Locate the retained files or use the identified historical writer with recorded provenance. Current-writer recordings and independently authored wire examples do not establish those historical files. |
| Model compatibility | Matching ASCII model/panel texture, owner collision fixtures, compiled models and external shuttle resources. | Recover the complete credited resource set and conversion provenance. A similarly named mesh is insufficient. |
| Independent numeric snapshots | Exact lens oracle, MPC element-epoch capture and mission state witnesses. | Recover the reference and its epoch/source identity. Newer elements or newly generated witnesses must be labelled separately. |
| Archived observing settings | Exact saved profile and companion optical-library expectations. | Recover the original profile, preserving its explicit user location. Neutral application defaults are tested separately. |

The detailed inventory, rather than this grouped table, determines which input belongs to each case. No expected population, frame count, source coordinate or reference tolerance is reduced to fit available files. Tests are not removed to lower a skip count. New independently authored checks can close a behavioral gap while an original archive case remains unavailable.

Optional culture artwork is a separate corpus-integrity issue, not another set of skipped tests. The [recovery recipe](TEST_COVERAGE.md#recover-available-culture-artwork) restores 171 previously absent images, yielding 299 available images for unchanged definitions. Six references remain absent from the pinned upstream tree. Keep those image-availability results separate from both the 34-case inventory and managed line coverage.

Public data-free CI intentionally reports more optional skips than a local scientific installation. Compare identical profiles and unique case names; do not add Debug/Release counts or collapse data-dependent theory discovery into a misleading total.
