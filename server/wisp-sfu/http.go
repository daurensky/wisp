package main

import (
	"bytes"
	"encoding/json"
	"io"
)

func jsonReader(value any) io.Reader { data, _ := json.Marshal(value); return bytes.NewReader(data) }
