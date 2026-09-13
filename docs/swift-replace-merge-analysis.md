# Swift content: replace/merge analysis

`swift-starter.json` ships the split documented here; the E2E pipeline asserts the full
merge-subtree contract and the round-trip is verified end to end. This document analyses
every content item in the Swift 2.2 baseline and assigns each an underpinned replace/merge
decision. The principle behind it: **a replace run must never overwrite the customer's
content surfaces (the homepage above all), while everything platform-wired must stay
identical on every environment.**

## 1. What the modes actually do (the facts decisions rest on)

These semantics are implemented and E2E-verified; the decisions below depend on them:

| Behaviour | Replace | Merge |
|---|---|---|
| Page missing on target | created, full content | created, full content |
| Page exists on target | **overwritten** (source wins, field-level) | **merged** — only fields that are still empty are filled; customer edits always win |
| Page exists on target but not in YAML | left alone — deserialize is an **upsert, never a mirror**; nothing is ever deleted | left alone |
| Links into the other mode's pages | deferred and resolved during that mode's pass (cross-mode link deferral) | same |

Three consequences worth spelling out:

- Customer-**added** pages under a replace-managed path survive every replace run (they are
  simply unmanaged). The replace risk is only to **edits of pages the YAML contains**.
- A page can be *structurally required* and still live in merge: merge creates it with full
  wiring on first land, and replace-side links to it resolve via cross-mode deferral. What
  merge gives up is **update propagation** — later solution changes to that page's
  *existing* paragraphs do not reach existing environments.
- **Structure self-heals in both modes.** Create-if-missing applies at every level — page,
  grid row, paragraph — independent of mode (the merge/overwrite strategies only govern
  *updates*). A merge-managed page whose row or paragraph is missing on the target gets it
  back on the next pass, and **new** rows/paragraphs the solution adds to a merge page do
  propagate. Merge is therefore already a "partial" mode: presence and structure are
  guaranteed, content inside is customer-owned.

## 2. Decision framework

Four litmus tests, applied per subtree:

1. **Who edits it after go-live?** Routine content/marketing edits by the customer → merge.
2. **Does the platform reference it by wiring?** Navigation tags, module attach points,
   checkout/login flows, pages referenced by id from area settings or app configs → replace.
3. **Must solution updates propagate?** If a partner iterating on the solution needs the
   change to land on every environment (checkout fix, component config) → replace.
4. **Is it example material?** Starter/demo content the customer is expected to replace,
   duplicate, or delete → merge.

**Tie-break** (page is both wired *and* customer-edited, e.g. Home): **merge wins.**
Rationale: merge still creates the page fully wired on first replace, links to it keep
resolving, and the customer's ownership of their primary content surfaces is the higher
value. Structural redesigns ship through templates/design Files, not through content
paragraphs, so the lost update propagation is acceptable. The reverse mistake (replace
stomping a customer's homepage) is a production incident.

A third bucket exists and stays out of scope by *not having a predicate*: environment data
(orders, users, logs, consents) plus the field-level carve-outs already in the starter
config (`excludeFields`, `excludeXmlElementsByType` for domains, API keys, mail
recipients).

## 3. Inventory and decisions

Page counts and paragraph counts (¶) from the verified Swift 2.2 baseline (Area 3,
120 pages).

### Navigation section (the public site)

| Subtree | ¶ | Wiring | Decision | Rationale |
|---|---|---|---|---|
| **Home** (6869) | 24 | frontpage binding (area-level, env-owned anyway) | **Merge** | The customer's primary marketing surface; first thing they edit. Must exist on day one — merge creates it. Re-replaces must never stomp it. |
| **Home Machines** (4897, inactive) | 23 | none | **Merge** | Inactive demo alternative homepage; example material to repurpose or delete. |
| **Shop** (5862) + Product List / Product Details | 1+5+6 | NavTag `Shop`, `ProductListPage`, `ProductDetailPage`; catalog module paragraphs | **Replace** | Pure commerce wiring; paragraphs are app configuration, not copy. PLP/PDP fixes must propagate. (Customer banner tweaks on PLP are the known middle case — see §5.) |
| **About** (107) + Contact + Thank you | 16+5+1 | contact form → form receipt emails (replace) | **Merge** | Marketing copy and the contact flow's visible content. Cross-mode links to email pages resolve via deferral. |
| **Posts** (8345) + Reviews / Buying guides / Travel guides (20 articles, 2¶ each) | ~42 | none | **Merge** | The canonical starter-blog example. |
| **Navigation** (88) structure + **Express Buy** | 5 | NavTag `ExpressBuyPage` | **Replace** | The navigation scaffolding (Secondary/Footer Navigation folders, Languages → Preferences) is solution structure; Express Buy is a wired feature page. |
| └ **Find dealers** (1247) | 1 | none | **Merge** | Customer-owned store-locator content under the nav scaffold. |
| └ **Footer Navigation / About the shop** (About us, Terms, Employees, Privacy policy) | 0–4 | none | **Merge** | Legal and company copy — quintessential customer content. A replace must never overwrite an updated privacy policy. |
| └ **Footer Navigation / Help and info** (FAQ, Delivery, Cookie notice) | 6–7 | none | **Merge** | Same: help copy the customer maintains. |
| └ **Footer Navigation / Languages** (+ Preferences) | 0 | language selector flow | **Replace** | Framework page backing the language switcher. |

### Customer center section

| Subtree | Decision | Rationale |
|---|---|---|
| **Sign in** (+ Forgot password, Create user profile, confirmations) | **Replace** | Login/registration flow; module paragraphs; solution fixes must propagate. |
| **Customer center** (Overview, Account, CSR, My orders/carts/quotes/wallet/favorites/profile/addresses…) | **Replace** | The entire self-service portal is wired (NavTags `SavedCardsPage`, `ViewProfile`, `EditProfile`) and never customer-authored. |
| **Shopping cart** (Cart, Empty cart, Checkout anonymous/user, Quote checkout, Add credit card) | **Replace** | Checkout is the most correctness-critical flow in the solution; NavTag `AddCreditCardPage`. |

### Swift Setup section

| Subtree | Decision | Rationale |
|---|---|---|
| **Header / Footer** (Desktop/Mobile Header, Desktop/Mobile Footer; 5–8¶ each) | **Merge** | Customers routinely edit USP texts, phone numbers and footer columns. The chrome is bound from area settings (HeaderDesktop/Mobile, FooterDesktop/Mobile item fields), and that wiring is safe in merge: merge guarantees presence and self-heals structure (§1), the area bindings ship at replace and resolve via cross-pass link deferral, and embedded-`/` path matching ("Header / Footer") is covered by unit test. |
| **Product Components** (Product List, Product List Card, Product Info) | **Replace** | PLP/PDP component configuration; partner-iterated, never customer-authored. |
| **Service Pages** (CartService, CartSummary, search results, Related products list/slider, Variant Selector, Favorites service) | **Replace** | The user's own example of clear replace candidates: invisible framework endpoints, all NavTag-wired. |
| **Search result page** | **Replace** | NavTag `ContentSearch`. |

### Emails section

| Subtree | Decision | Rationale |
|---|---|---|
| **System emails** (Order confirmation 30¶, Welcome, Back in stock, Form receipts) | **Replace** | Transactional templates wired into checkout/user-management/stock-notification settings. Environment-specific bits (sender, recipient) are already carved out at field level (`excludeXmlElementsByType`). Copy edits by customers exist but transactional correctness and propagation win. |
| **Newsletter Emails** root + **Unsubscribe confirmation page** | **Replace** | The folder scaffold and the unsubscribe page are wired into the newsletter flow. |
| └ **Swift Newsletters - Light / - Dark** (example campaigns, Sale 22¶, Announcement 31¶) | **Merge** | Example campaigns the customer duplicates and rewrites — classic starter material. The carve sits at the folder level (not the root) so the unsubscribe page ships via replace — every page must be covered by exactly one mode. |

### Presets section

| Subtree | Decision | Rationale |
|---|---|---|
| **Page presets** (Home pages → Home preset, `IsTemplate`, 20¶) | **Replace** | Part of the solution's design system: presets are the partner's curated starting points for new pages. Customer-created presets are additions and survive (upsert semantics). |

## 4. The predicate set

One replace predicate keeps the "everything is managed unless carved out" default; the
carve-outs become explicit merge predicates. SqlTable predicates are unchanged (framework
tables replace, catalog merges).

```jsonc
// Replace — Content
{ "name": "Site framework", "mode": "Replace", "providerType": "Content", "areaId": 3,
  "path": "/", "includeLanguageLayers": true,
  "excludes": [
    "/Home", "/Home Machines", "/About", "/Posts",
    "/Header / Footer",
    "/Navigation/Secondary Navigation/Find dealers",
    "/Navigation/Footer Navigation/About the shop",
    "/Navigation/Footer Navigation/Help and info",
    "/Newsletter Emails/Swift Newsletters - Light",
    "/Newsletter Emails/Swift Newsletters - Dark"
  ] }

// Merge — Content (one predicate per customer-owned subtree)
{ "name": "Homepage",            "path": "/Home" }
{ "name": "Homepage (machines)", "path": "/Home Machines" }
{ "name": "Site chrome",         "path": "/Header / Footer" }
{ "name": "About pages",         "path": "/About" }
{ "name": "Starter blog posts",  "path": "/Posts" }
{ "name": "Find dealers",        "path": "/Navigation/Secondary Navigation/Find dealers" }
{ "name": "Footer: about the shop", "path": "/Navigation/Footer Navigation/About the shop" }
{ "name": "Footer: help and info",  "path": "/Navigation/Footer Navigation/Help and info" }
{ "name": "Newsletter examples (light)", "path": "/Newsletter Emails/Swift Newsletters - Light" }
{ "name": "Newsletter examples (dark)",  "path": "/Newsletter Emails/Swift Newsletters - Dark" }
// (all merge Content predicates: mode=Merge, areaId=3, includeLanguageLayers=true,
//  same acknowledgedOrphanPageIds list as today)
```

The bottom line: **well over a hundred customer-content paragraphs across Home, About,
the site chrome (Header / Footer), footer legal/help pages, Find dealers and the
newsletter examples land once and are customer-owned from then on.** Everything wired
replaces identically to every environment.

## 5. Notes and caveats

1. **Why the chrome can live in merge.** "Must exist or the solution breaks" is satisfied
   by merge itself: presence and structure self-heal at page/row/paragraph level
   (create-if-missing is mode-independent, §1) while customers keep their chrome edits.
   Path matching is pure string prefix with a `/`-boundary, both sides built from the same
   menu texts, so the literal `/` in "Header / Footer" matches correctly (unit-tested in
   `ContentCoverageEvaluatorTests`).
2. **Shop PLP/PDP marketing slots.** If customers routinely add banners to the Shop pages,
   those edits sit on replace-managed pages and would be overwritten. Mitigation today:
   customer banners as *new* paragraphs survive only if replace YAML doesn't carry the page
   (it does). If this bites, the fix is finer granularity (paragraph-level merge on
   replace), not moving Shop to merge.
3. **Changing a page's mode needs no data migration.** A page that already exists on the
   target keeps existing; a merge pass merges (fill-empty) it from then on — exactly the
   desired behaviour.
4. **E2E contract.** The pipeline's Step 18 asserts the full split: replace YAML contains
   none of the merge subtrees, merge ships them all, the target lands them, and the
   unsubscribe page ships via replace.
5. **The area definition ships only via the whole-area replace predicate**
   (`path: "/"` owns area properties/item fields), enforced by the deserializer's
   area-state ownership rule.

## 6. Decision record

| # | Decision | Underpinning |
|---|---|---|
| D-1 | Merge wins ties for customer-edited wired pages | Merge creates fully-wired pages on first land; cross-mode links resolve; stomping customer content is the worse failure |
| D-2 | Home, About, footer legal/help, Find dealers, newsletter examples → merge | Litmus test 1 + 4 |
| D-3 | Shop, Customer center, Shopping cart, Swift Setup, System emails, presets, nav scaffold → replace | Litmus tests 2 + 3 |
| D-4 | Header / Footer → merge | Merge self-heals structure (presence guaranteed); embedded-slash path matching verified; customer owns chrome copy |
| D-5 | No third Content mode needed | Upsert (no-delete) semantics already protect customer additions under replace paths |

## 7. Exclusion levels and Admin UI visibility

The replace/merge split above is the coarse partition. Below it sits a second axis:
**exclusions**, which carve fields, settings, columns and rows out of otherwise-managed
content. A page can be "replace-managed" by path algebra and still be only partially
managed in practice — the canonical case is the **Shopping cart** page: the replace
predicate covers it, but the starter config excludes ~20 `eCom_CartV2` module-settings
elements (mail sender/recipient, error messages, `DefaultPaymentId`/`DefaultShippingId`,
payment/delivery type bindings). Those settings stay local per environment. A user who
only sees the path-level picture would wrongly assume a cart settings edit is replace-managed.

The principle: **every exclusion level must be visible in the Admin UI at the place where
the user would otherwise be misled** — and the cue must be selective enough that the
actionable signal (replace overwrites) is never drowned.

### The exclusion levels

| Level | Config key | Scope | Typical use in the starter config |
|---|---|---|---|
| Path | predicate `excludes` | Whole subtrees carved out of a Content predicate | The merge subtrees carved out of `Site framework` |
| Field, by item type (global) | `excludeFieldsByItemType` | Named item fields, wherever the type appears, all predicates | `Swift-v2_Master` (14 fields): the website-settings env values — see below |
| XML element, by type (global) | `excludeXmlElementsByType` | Elements inside module-settings / provider XML, keyed by paragraph module system name or URL provider type | `eCom_CartV2` (20 elements), `UserAuthentication`, `UserCreate`/`Edit`/`View`, `eCom_CustomerExperienceCenter` |
| Field, per predicate | predicate `excludeFields` | Every item/row the predicate touches | `Site framework`: area-level env values (domains, GTM, favicon, social ids); SqlTable: credential columns |
| XML element, per predicate | predicate `excludeXmlElements` | Elements inside that predicate's `xmlColumns` | Payment gateway / shipping service credential elements |
| Area column | predicate `excludeAreaColumns` | Columns on the `[Area]` SQL row | Frontpage binding, shop/language/currency bindings, CDN host |
| Row | predicate `where` (SqlTable) | Which rows serialize at all | Filtering user tables to roles |
| Runtime (code) | `RuntimeExcludes` registry | Always-on column strips | `UrlPathVisitsCount`, `EcomShops` index columns |
| Opt-back-in | predicate `includeFields` | Re-includes a runtime-excluded column | Test-only index-config capture |

### Why the starter populates `excludeFieldsByItemType` with `Swift-v2_Master`

A field-name scan across every Swift 2.2 item-type table shows the environment-specific
item fields concentrate on ONE type: `Swift-v2_Master`, the website-settings item — GTM
container id, Google API key and site verification, Pinterest domain verification,
Facebook/Twitter app and site ids, favicon/touch icon, meta site name/image, and
`CustomHeadInclude` (free-form head HTML, in practice tracking and verification snippets).
The remaining item types carry content and design choices, not environment identity; the
page-link fields some types hold travel through GUID link resolution, not excludes.

The starter therefore scopes these fields where they belong:

```jsonc
"excludeFieldsByItemType": {
  "Swift-v2_Master": [
    "GoogleTagManagerID", "Favicon", "AppleTouchIcon",
    "FacebookAppId", "Fb_app_id",
    "Google_APIKey", "Google_Site_Verification", "P_domain_verify",
    "TwitterSite", "Twitter_Site",
    "MetaSiteName", "MetaImage", "MetaImageALT",
    "CustomHeadInclude"
  ]
}
```

The `Site framework` predicate's flat `excludeFields` keeps only the `Area*` values
(domain, robots, noindex) — SQL-row state on the Area table, not item fields. Scoping by
type rather than by bare name means a paragraph item type that happens to declare a field
called `Favicon` is no longer silently carved out, and the inventory/cue machinery can
attribute every exclusion to the type that owns it. When a solution adds its own item
types, the same test applies per field: *does the correct value differ per environment*
(keys, tracking ids, embed snippets, index names) → exclude by type; *is it content the
customer owns* → leave it in and let the replace/merge split govern it.

### Where each level is visible

| Level | Admin UI surface |
|---|---|
| Path excludes | Content tree: sync-slash icon, tooltip names the carved-out paths. Predicate edit screen, where excludes are picked from the area's live page tree (no free-text paths — a typo would silently exclude nothing). |
| Global by-type excludes (both dicts) | Four surfaces, each one click from the next: (1) the **settings screen** shows a per-type inventory ("These stay local per environment: eCom_CartV2 (21 settings), …"); (2) the **Item Type Excludes** / **Embedded XML Excludes** sub-nodes edit them; (3) the **content tree** downgrades affected pages to the partial (sync-slash) icon and the right-click menu carries "View excluded fields: eCom_CartV2 (21 settings)" opening the exact exclusion list; (4) the **editing screens** — visual editor, page properties, paragraph and grid-row dialogs — keep a one-line verdict alert and add a clickable header chip per carved-out type ("eCom_CartV2 — 21 settings stay local — view") that opens the same list; screens without a header strip of their own get one created so the chip is always clickable. |
| Per-predicate `excludeFields` / `excludeXmlElements` / `excludeAreaColumns` | Predicate edit screen (dual-list pickers from the live schema). On the commerce settings screens (payment, shipping, …) the predicate's exclusions render as a clickable "Stays local" chip opening the predicate editor. Deliberately **not** flagged per tree page: these are area-level values that apply to everything the predicate covers — flagging them would mark the whole tree partial and drown the per-page signal. |
| `where`, `includeFields` | Predicate edit screen. |
| RuntimeExcludes | Documentation ([runtime-exclusions](runtime-exclusions.md)); curated in code by PR. |

The tree/alert cue keys on the **types actually present on a page** (page item type, URL
provider type, paragraph item types, paragraph module system names) matched against
non-empty entries in the two global dicts. That is what makes it selective: with the
starter config, the cart page, the checkout pages and the user-management pages show
partial; an ordinary content page does not.

Every click-through opens the read-only **"Stays local"** panel (a SlideOver, so it works
from any editing context including the SlideOver-hosted editors) — the cues inform **all**
backend users, including content editors without Settings access. The "Manage exclusions"
shortcut into the settings editors appears only for administrators.

### Non-content surfaces: which cues are worth having

Content pages have one generic visual surface — the tree — so one cue covers all pages.
SqlTable-managed data (payment methods, shipping methods, order flows, catalog rows) is
edited on per-domain screens, so cues there are per-screen work and must be chosen
selectively. The selection rule: **a cue earns its place when an editor would otherwise
change a value and be silently overwritten (replace), or assume a value syncs when it
stays local (excluded credentials).**

| Surface | Verdict | Rationale |
|---|---|---|
| Payment / shipping method edit screens (`EcomPayments`, `EcomShippings`) | **Cue shipped** — screen alert mirroring the content editor alert: "This payment method is replace-managed by 'EcomPayments'. Changes here are overwritten by the next replace run…". The predicate's `excludeFields` / `excludeXmlElements` render as a clickable "Stays local" header chip opening the predicate editor. Replace warning always shows; a merge-mode predicate shows the info alert only when `showMergeIndicators` is on. | Highest mislead risk among SqlTable data: method config replaces, excluded credentials don't — both failure directions are plausible for an editor who can't see the predicate. |
| Country / currency / ecommerce-language / shop / order-flow / order-state edit screens | **Cue shipped**, same shape (entity noun per screen) | Same trap, lower stakes: a VAT-rate or currency-rounding tweak on a target is silently overwritten by the next replace. These are settings screens (one entity per screen), so the cue carries no list noise. VAT group: pending — its edit screen type is not public in the pinned DW package version. |
| URL path / redirect rows | **No cue** | Bulk rows (catalog-like); the screen type also sits in a package not pinned by this project. The predicate list covers it. |
| Product / group catalog screens (merge SqlTable predicates) | **No per-row cue** | Merge semantics already protect local edits; thousands of rows would all carry the same badge — pure noise. The settings screen's predicate summary is the right altitude. |
| Area/website settings dialog | **No extra cue** | Area state ships only via the whole-area replace predicate (§5.5); `excludeAreaColumns`/`excludeFieldsByItemType` carve the env-owned values, and those fields are env-owned by definition — an editor changing the domain on a target is doing the right thing. A cue would warn about the safe case. |

Across all providers, the **settings screen inventory** answers "what stays local?" in
one place, and the predicate edit screens carry the per-table detail.

## 8. Coverage of DynamicWeb issue #196

[dynamicweb/DynamicWeb#196](https://github.com/dynamicweb/DynamicWeb/issues/196) collects
the field experience with DW's built-in Deployment tool. Point-by-point against this
serializer:

| # | Issue #196 point | Status here |
|---|---|---|
| 1 | Replace actions should live where users work, not in one tool screen | **Covered** — mode alerts on every content editing surface and on the commerce settings edit screens (payment, shipping, country, currency, language, shop, order flow, order state — §7). Bulk/list surfaces are deliberately cue-free (§7 verdicts). |
| 2 | Permissions cannot be replaced; groups lack GUIDs | **Partial** — page permissions serialize with the page and re-resolve groups by name on the target; missing groups are skipped with a logged warning ([permissions](permissions.md)). User groups themselves are not shipped as identity-mapped entities. |
| 3 | SQL provider copies raw page IDs, "rendering it useless" | **Covered** — content cross-references travel as GUIDs (`UniqueId`); `resolveLinksInColumns` rewrites `Default.aspx?ID=N` references in SqlTable rows (e.g. `UrlPath` redirects) to target IDs ([link-resolution](link-resolution.md)). |
| 4 | Area provider: env-specific values create false diffs; needs property blacklist/whitelist | **Covered** — `excludeFields` + `excludeAreaColumns` carve exactly the env-owned area values (domains, GTM, frontpage/shop bindings); the starter config ships the curated list. |
| 5 | Pages/items compared as blobs, no tree representation | **Covered** — one YAML file per page/row/paragraph in a folder tree mirroring the content tree; field-level diffs in Git. |
| 6 | Page provider lacks parent/root + include-subpages scoping | **Covered** — Content predicates are exactly root-path + subtree (with `excludes`); an inline `Scope` on `Serialize`/`Deserialize` narrows a single call to any subtree. |
| 7 | Row/paragraph providers lack page context | **Covered by design** — the page is the unit that ships; rows/paragraphs ship inside their page and inherit its mode (the editing alerts state this). No independent row/paragraph predicate, deliberately. |
| 8 | Item provider ships IDs instead of GUIDs → broken references | **Covered** — paragraph/page references resolve through the GUID-based `ReferenceResolver`; ButtonEditor JSON, `Default.aspx?ID=` and raw-numeric formats are all rewritten. |
| 9 | Module/app XML (cart!) mixes env settings with replace-managed config; XML compared as text blob | **Covered** — `xmlColumns` pretty-printing for readable diffs, `excludeXmlElementsByType` strips the env-specific elements (`eCom_CartV2` et al.), and the partial-managed cues (§7) make the split visible to editors. This is the issue's shopping-cart example, handled at element granularity. |
| 10 | Packages include all data groups regardless of relevance | **Covered** — predicates define exactly what ships; deserialize is an upsert, never a mirror. |
| 11 | Binary zip packages aren't Git-friendly | **Covered** — the primary format IS the on-disk YAML tree, committed and PR-reviewed; zips exist only as a transfer format (`PackageDownload`, `PackageUnzip`). |
| 12 | JSON contains all data, not just differences | **Different model, same goal** — baselines are declarative desired state, so files carry full state, but Git diffs show exactly what changed per PR, and merge-mode merge touches only still-empty fields on the target. A diff-shipping model is not planned. |
| 13 | Adoption: hard for technical users, harder for others | **Addressed continuously** — admin UI predicates/excludes editing, tree + editor cues, settings inventory, log viewer with remediation advice. |
| 14 | (Context: raised by implementation teams for DW10) | n/a — this project is that response. |

Open item from this mapping: identity-mapped user-group shipping (#2) is the one place
where the issue's ask is not yet fully met.

## 9. The newcomer questions and how the tool answers them

The cues in §7 answer "what happens to THIS thing when someone syncs?" at the place where
the thing is edited. The questions a developer meeting the concept for the first time asks
next, and where the answer lives:

1. **"What would happen if I ran this?"** — *Preview deserialize (dry run)* / *Preview
   merge (dry run)* on the settings screen run the full pipeline without writing: the
   result reports would-create/update/skip per predicate, and the log carries per-field
   `[DRY-RUN]` lines (Log Viewer). Also available per call: `?dryRun=true` on the
   Management API. A structured side-by-side diff screen remains a candidate on top of
   the log-based preview.
2. **"When did this last sync?"** — the settings screen's *Sync history* line ("Last
   replace received: 11 Jun 2026 21:38 (created 0, updated 278, failed 0) · Last merge
   received: never") and the same timestamp on every replace-managed editing alert. Read
   from the run-log summaries; dry runs don't count.
3. **"Is this page in sync right now?"** — drift v1: a replace-managed page edited on this
   environment after the last replace run gets "this page changed on this environment after
   that replace run — the next replace run will overwrite those changes" on the editing alert and
   tree tooltip. Timestamp-based (page audit date vs. last replace run, 5-minute grace margin);
   a true per-page YAML diff ("Compare with baseline") remains the v2 candidate.
4. **"Where do I start?"** — a fresh install creates an EMPTY configuration (nothing
   syncs until predicates exist), and the settings screen swaps its actions for a *Get
   started* group: "Start from the Swift starter…" (pick the website; the embedded
   starter is written with its Content predicates rebound) or "Create empty
   configuration".
5. **"What do these words mean?"** — [glossary.md](glossary.md): baseline, predicate,
   replace/merge, managed/partial, stays local, dry run, drift.
6. **One coverage picture** — the settings screen's *Coverage* line: "Area 3: 75 pages
   replace, 48 merge, 0 unmanaged · Tables: 16 replace, 8 merge", computed through the same
   evaluators as the tree icons (skipped with a note above 2,000 pages).
