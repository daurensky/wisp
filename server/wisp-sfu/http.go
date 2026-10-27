package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
)

func jsonReader(v any) io.Reader { b, _ := json.Marshal(v); return bytes.NewReader(b) }
func fetchJSON(c *http.Client, r *http.Request, v any) error {
	resp, err := c.Do(r)
	if err != nil {
		return denied
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return denied
	}
	return json.NewDecoder(io.LimitReader(resp.Body, 512*1024)).Decode(v)
}
