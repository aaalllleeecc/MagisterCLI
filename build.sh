#!/usr/bin/env bash
# Run the Magister CLI.
#
#   ./build.sh                  schedule for the next 7 days
#   ./build.sh afspraken 14     schedule for 14 days
#   ./build.sh berichten 10     last 10 messages
#   ./build.sh cijfers          latest grades
#   ./build.sh studiewijzers
#   ./build.sh logout           delete the saved session
#
# The password is NOT stored here. It is only asked for when there is no valid
# saved session (see ~/.config/magister/session.json). To skip the prompt in a
# non-interactive run, export MAGISTER_PASSWORD in your shell first.
#
# Optional overrides:  MAGISTER_SCHOOL, MAGISTER_USER, MAGISTER_HOST, MAGISTER_VERBOSE=1

set -euo pipefail

# Work from the project folder, even when called through a symlink or an alias
cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"

SCHOOL="${MAGISTER_SCHOOL:-RSG Pantarijn}"
USERNAME="${MAGISTER_USER:-622318}"

exec dotnet run -v q -- "$SCHOOL" "$USERNAME" "$@"