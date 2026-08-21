# Reviewable pull request size

A pull request carries one behavior that a reviewer can understand and verify
without reconstructing several changes at once. The numeric gate catches a
slice that is probably too broad. It does not define whether the slice is good.

`.github/pr-size-policy.json` is the single source for the limits, exception
label, and ignored paths. Tests and documentation count because they are part
of the behavior. Moving either into another PR merely to pass the gate is not
an acceptable split.

## Planning gate

Before `ready-for-agent`, the issue names:

- the independently verified behavior;
- the expected changed files;
- the expected additions plus deletions;
- the verification method (unit / containers / live) and the command, test, or
  live check that verifies the behavior; and
- the Paths list the ticket owns.

Before coding, the implementation agent compares that forecast with the
policy. If either limit is exceeded, the agent stops and proposes smaller
vertical slices. A vertical slice includes the behavior, its tests, and the
documentation needed to use or review it.

Before requesting review, the agent measures the current PR base and head and
records the measured output in the PR template.

## Exception

Only Hari may apply the policy's exception label. The PR body must also replace
`Approved exception justification: N/A` with the reason the complete slice
cannot be smaller. CI fails an exception label without that written reason.
Agents do not apply the label or change branch protection.

An exception permits size. It does not permit several unrelated behaviors.

## Stacked children

A stacked child starts from an unmerged parent branch. After the parent merges,
the old parent commits can appear in the child's diff because GitHub compares
the child with the new base.

Before review continues, the agent rebases the child onto the current base or
rebuilds the child from it. The agent then reruns CI and checks GitHub's
changed-files view. The child is ready only when that view contains child-owned
changes. Agents never rebase or force-push a contributor branch automatically.

Governance review treats inherited parent changes, an unjustified limit excess,
or displaced tests and docs as findings even when the status check is green.
