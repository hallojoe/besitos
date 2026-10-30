# Readability Guidance

## Method extraction

Prefer a high-level workflow such as `ValidateOrder(); CalculateTotal(); SaveOrderAsync();` when those steps each contain meaningful logic. Keep a simple one-line expression inline when extraction would only force readers to jump between methods.

Avoid arbitrary rules such as five lines per method. Aim for one cohesive responsibility and a consistent level of abstraction.

## Naming

Prefer `pendingOrders` to `data`, `validationResult` to `result` when ambiguous, and `ProcessPaymentAsync` to `HandleStuff`. Short local names such as `order` are fine when the scope is clear.

## Control flow

Use early returns for invalid or exceptional cases. Avoid stacking conditional operators or complex LINQ chains that obscure business logic.

## Modern syntax

Follow the target framework, `LangVersion`, and local style. A primary constructor is useful when its dependencies remain clear; use a regular constructor when validation or initialization needs more space. Collection expressions work well when the target type is evident. Prefer pattern matching and LINQ when they express a simple condition or transformation; use ordinary branches and loops when they make the flow easier to inspect.

## Comments

Bad: `// Check whether customer is active` before `if (!customer.IsActive)`.
Good: `// Suspended customers keep existing subscriptions but cannot create new ones.` when that rule is not otherwise evident.

## XML docs

Document contracts, important preconditions, side effects, and non-obvious exceptions. Do not require `<param>` and `<returns>` tags that merely restate names. When an interface owns the contract, put the useful `<summary>` on the interface and use `<inheritdoc />` on its implementation. Format multi-line summaries consistently with the surrounding code.

## Review questions

Can a reviewer understand the flow quickly? Are names precise? Did extraction improve readability? Are comments informative? Is any abstraction unnecessary?
