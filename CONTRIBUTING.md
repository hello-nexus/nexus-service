# Contributing

## License

This repository is licensed under AGPL-3.0 (see [`LICENSE`](LICENSE)). By
contributing, you agree your contributions are licensed under AGPL-3.0 and the
terms in [`CLA.md`](CLA.md).

## Tested on hardware, or not merged

Nexus drives real devices. A change that compiles and passes the unit tests
has not been tested until it has run against the hardware it touches.

Every pull request that changes anything the service ships (code, data files,
bundled binaries, the project file) must have been built, run and tested by
the contributor, on the contributor's own machine, with the affected device
attached. Documentation-only and test-only changes are exempt. There is no lab
that does this for you. A pull request without that is closed, whatever its
size.

1. Build from your branch and run the service on your machine.
2. Exercise the change against the physical device. If more than one model is
   affected, test each one you have and list the ones you could not.
3. Fill in the Hardware validation section of the pull request template. If
   the change touches no device (a route, the updater, the cloud client),
   say so and describe what you ran instead. Write down what you observed,
   not what you expect. "Brightness 0 to 100 applied on a Galahad II Vision,
   firmware V2.01.041" is a validation note; "should work" is not.

If you cannot test a change on the hardware, do not send it. Open an issue and
describe what you found.

## If an AI agent writes the change

The same rules apply, and the person who opens the pull request answers for
them. An agent cannot run the service against your hardware, so the validation
section describes what you ran, on your machine, in your words. A pull request
whose validation text does not match what was run is closed.

## Standards

- One topic per pull request.
- `dotnet test` passes locally. New behaviour comes with tests. A bug fix comes
  with a test that fails without it. CI runs the same build and suite on every
  pull request.
- The build stays warning-free.
- Native AOT is the shipping configuration: no reflection-based serialization,
  no dynamic code. Types that cross JSON go through the source-generated
  contexts in `src/Serialization/`.
- Match the surrounding code. Do not reformat, rename or reorganize anything
  the change does not need.
- Keep `README.md` true. If the change alters how the service is built, run or
  laid out, update the README in the same pull request.
- Read every line you submit, generated or not, and be able to say why it is
  there.

## Workflow

1. Fork and branch from `main`.
2. Open the pull request against `main` and complete every section of the
   template.
3. Confirm in the pull request that you have read and agree to
   [`CLA.md`](CLA.md).
4. Answer review with new commits. After any change, test on the hardware
   again and update the validation section.
