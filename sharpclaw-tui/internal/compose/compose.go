package compose

import (
	"bytes"
	"fmt"
	"os/exec"
	"strings"
)

type Runner struct {
	ComposeCommand string
	ComposeFile    string
	WorkingDir     string
}

func (r Runner) args(extra ...string) []string {
	base := []string{"compose"}
	if strings.TrimSpace(r.ComposeFile) != "" {
		base = append(base, "-f", r.ComposeFile)
	}
	return append(base, extra...)
}

func (r Runner) run(extra ...string) (string, error) {
	cmd := exec.Command(r.ComposeCommand, r.args(extra...)...)
	cmd.Dir = r.WorkingDir

	var out bytes.Buffer
	var errOut bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &errOut

	if err := cmd.Run(); err != nil {
		if errOut.Len() > 0 {
			return "", fmt.Errorf("%w: %s", err, strings.TrimSpace(errOut.String()))
		}
		return "", err
	}

	if errOut.Len() > 0 {
		if out.Len() == 0 {
			return strings.TrimSpace(errOut.String()), nil
		}
		return strings.TrimSpace(out.String()) + "\n" + strings.TrimSpace(errOut.String()), nil
	}

	return strings.TrimSpace(out.String()), nil
}

func (r Runner) Start(services []string, build bool) (string, error) {
	args := []string{"up", "-d"}
	if build {
		args = append(args, "--build")
	}
	args = append(args, services...)
	return r.run(args...)
}

func (r Runner) Stop(services []string, volumes bool) (string, error) {
	if len(services) > 0 {
		args := append([]string{"stop"}, services...)
		return r.run(args...)
	}

	args := []string{"down"}
	if volumes {
		args = append(args, "-v")
	}
	return r.run(args...)
}

func (r Runner) Status() (string, error) {
	return r.run("ps")
}
